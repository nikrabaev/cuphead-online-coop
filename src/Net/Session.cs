using System;
using CupheadOnline.Sync;
using UnityEngine;

namespace CupheadOnline.Net
{
    public enum Role { None, Host, Client }

    public enum LinkState { Idle, Waiting, Connecting, Connected, Failed }

    /// <summary>Owns the one network link and routes messages to the host/client sync modules.</summary>
    public static class Session
    {
        public static Role Role { get; private set; }
        public static LinkState State { get; private set; }
        public static string Status { get; private set; }
        public static Connection Connection { get; private set; }
        public static string RemoteName { get; private set; }

        private static double connectStarted;
        private static string pendingStop;
        private static double lastHelloSent;
        private static string localName;

        public static bool IsHost { get { return Role == Role.Host && State == LinkState.Connected; } }
        public static bool IsClient { get { return Role == Role.Client && State == LinkState.Connected; } }

        static Session()
        {
            Status = "Offline";
        }

        public static void StartHost(IDatagramTransport transport, string playerName)
        {
            Stop("restart");
            localName = playerName;
            Role = Role.Host;
            State = LinkState.Waiting;
            Connection = new Connection(transport);
            Connection.OnMessage += HostOnMessage;
            Status = "Hosting — waiting for a player to join";
            Plugin.Log.LogInfo("Hosting: " + transport.GetType().Name);
        }

        public static void StartClient(IDatagramTransport transport, string playerName)
        {
            Stop("restart");
            localName = playerName;
            Role = Role.Client;
            State = LinkState.Connecting;
            Connection = new Connection(transport);
            Connection.OnMessage += ClientOnMessage;
            connectStarted = Connection.Now;
            lastHelloSent = -10;
            Status = "Connecting to " + transport.PeerDescription + " ...";
            Plugin.Log.LogInfo("Connecting: " + transport.PeerDescription);
        }

        public static void Stop(string reason)
        {
            if (Connection != null)
            {
                try { Connection.SendBye(); } catch { }
                Connection.Dispose();
                Connection = null;
                Plugin.Log.LogInfo("Session stopped: " + reason);
            }
            if (Role == Role.Host) HostSync.OnSessionEnded();
            if (Role == Role.Client) ClientMirror.OnSessionEnded();
            SteamSupport.EndHosting(); // clear rich presence / lobby on every kind of session end
            Role = Role.None;
            State = LinkState.Idle;
            RemoteName = null;
            Status = reason == "restart" ? Status : "Offline (" + reason + ")";
        }

        /// <summary>Called early every frame: receive and dispatch network traffic.</summary>
        public static void PreUpdate()
        {
            if (Connection == null) return;
            Connection.Receive();
            if (Connection == null) return; // a message may have ended the session
            if (pendingStop != null)
            {
                string reason = pendingStop;
                pendingStop = null;
                Connection.Flush();
                Stop(reason);
                return;
            }
            if (State == LinkState.Connected && Connection.TimedOut)
            {
                Stop("connection timed out");
                return;
            }
            if (IsHost) RemoteInput.ReleaseIfStale(Connection.Now);
            if (Role == Role.Client && State == LinkState.Connecting)
            {
                double now = Connection.Now;
                if (lastHelloSent < 0)
                {
                    lastHelloSent = now;
                    SendHello(); // reliable: the connection layer retransmits until the host acks
                }
                if (now - connectStarted > 20.0)
                {
                    Stop("could not reach host");
                    State = LinkState.Failed;
                }
            }
        }

        /// <summary>Called late every frame: produce outgoing traffic and flush.</summary>
        public static void PostUpdate()
        {
            if (Connection == null) return;
            // A failing sync tick must never stop acks/pings from going out.
            try
            {
                if (IsHost) HostSync.Tick(Connection);
                if (IsClient) ClientMirror.Tick(Connection);
            }
            catch (Exception ex)
            {
                LogThrottled("Sync tick failed: " + ex);
            }
            Connection.Flush();
        }

        private static float lastErrorLog = -100f;
        private static int suppressedErrors;

        private static void LogThrottled(string message)
        {
            if (Time.unscaledTime - lastErrorLog < 5f) { suppressedErrors++; return; }
            lastErrorLog = Time.unscaledTime;
            Plugin.Log.LogError(message + (suppressedErrors > 0 ? " (+" + suppressedErrors + " suppressed)" : ""));
            suppressedErrors = 0;
        }

        private static void SendHello()
        {
            NetWriter w = new NetWriter(64);
            w.U8(Protocol.Version);
            w.Str(localName);
            w.Str(Application.version);
            Connection.SendReliable(MsgType.Hello, w);
        }

        private static void HostOnMessage(MsgType type, NetReader r)
        {
            switch (type)
            {
                case MsgType.Hello:
                {
                    if (State == LinkState.Connected) return;
                    byte version = r.U8();
                    string name = r.Str();
                    string gameVersion = r.Str();
                    if (version != Protocol.Version || gameVersion != Application.version)
                    {
                        NetWriter reject = new NetWriter(64);
                        reject.Str("Version mismatch (host mod v" + Protocol.Version + ", game " + Application.version + ")");
                        Connection.SendReliable(MsgType.Reject, reject);
                        // The rejected peer is pinned by the transport; end this hosting session after sending.
                        pendingStop = "rejected a player with a different mod/game version (they have " + gameVersion + ")";
                        return;
                    }
                    RemoteName = string.IsNullOrEmpty(name) ? "Player 2" : name;
                    State = LinkState.Connected;
                    Status = RemoteName + " joined — press any button on their side to spawn Mugman";
                    NetWriter welcome = new NetWriter(64);
                    welcome.Str(localName);
                    Connection.SendReliable(MsgType.Welcome, welcome);
                    HostSync.OnClientJoined(Connection);
                    Plugin.Log.LogInfo("Client joined: " + RemoteName + " from " + Connection.Transport.PeerDescription);
                    break;
                }
                case MsgType.Input:
                    if (State == LinkState.Connected) RemoteInput.OnInputMessage(r, Connection.Now);
                    break;
                case MsgType.SceneReady:
                    if (State == LinkState.Connected) HostSync.OnClientSceneReady(r.U16());
                    break;
                case MsgType.Bye:
                    if (State != LinkState.Connected) return; // e.g. a rejected peer saying goodbye
                    Stop("player 2 left");
                    break;
            }
        }

        private static void ClientOnMessage(MsgType type, NetReader r)
        {
            switch (type)
            {
                case MsgType.Welcome:
                    if (State == LinkState.Connected) return;
                    RemoteName = r.Str();
                    State = LinkState.Connected;
                    Status = "Connected to " + RemoteName;
                    ClientMirror.OnConnected();
                    break;
                case MsgType.Reject:
                    string reason = r.Str();
                    Stop(reason);
                    State = LinkState.Failed;
                    break;
                case MsgType.Bye:
                    Stop("host ended the session");
                    break;
                default:
                    if (State == LinkState.Connected) ClientMirror.OnMessage(type, r);
                    break;
            }
        }
    }
}
