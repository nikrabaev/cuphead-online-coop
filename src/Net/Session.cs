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
        /// <summary>The link dropped without a goodbye; the client is redialling, the host waits for it to come back.</summary>
        public static bool Reconnecting { get; private set; }

        /// <summary>How long a dropped player may take to come back before the session ends.</summary>
        public const float ReconnectWindow = 60f;

        private static Func<IDatagramTransport> openTransport;
        private static float reconnectDeadline;
        private static uint helloNonce;
        private static uint acceptedNonce;
        private static bool? runInBackgroundBefore;
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

        /// <param name="listen">Opens the listening transport; called again to wait for a dropped player.</param>
        public static void StartHost(Func<IDatagramTransport> listen, string playerName)
        {
            Stop("restart");
            IDatagramTransport transport = listen();
            openTransport = listen;
            localName = playerName;
            Role = Role.Host;
            Listen(transport);
            Status = "Hosting — waiting for a player to join";
            KeepRunningInBackground(true);
        }

        /// <param name="dial">Opens a transport to the host; called again to reconnect after a drop.</param>
        public static void StartClient(Func<IDatagramTransport> dial, string playerName)
        {
            Stop("restart");
            IDatagramTransport transport = dial();
            openTransport = dial;
            localName = playerName;
            Role = Role.Client;
            Dial(transport);
            Status = "Connecting to " + transport.PeerDescription + " ...";
            KeepRunningInBackground(true);
        }

        private static void Listen(IDatagramTransport transport)
        {
            State = LinkState.Waiting;
            Connection = new Connection(transport);
            Connection.OnMessage += HostOnMessage;
            Plugin.Log.LogInfo("Hosting: " + transport.GetType().Name);
        }

        private static void Dial(IDatagramTransport transport)
        {
            State = LinkState.Connecting;
            Connection = new Connection(transport);
            Connection.OnMessage += ClientOnMessage;
            connectStarted = Connection.Now;
            lastHelloSent = -10;
            helloNonce = (uint)new System.Random().Next(1, int.MaxValue);
            Plugin.Log.LogInfo("Connecting: " + transport.PeerDescription);
        }

        /// <summary>
        /// The link timed out (or the client redialled): keep the session and wait for it to come back. The host
        /// keeps its game and Mugman, and listens again; the client keeps its mirrored scene, and redials.
        /// </summary>
        private static void LoseConnection(string why)
        {
            Plugin.Log.LogInfo("Connection lost (" + why + "); reconnecting for up to " + ReconnectWindow + " s");
            Connection.Dispose(); // no goodbye: the other side should keep waiting for us
            Connection = null;
            Reconnecting = true;
            reconnectDeadline = Time.realtimeSinceStartup + ReconnectWindow;
            try
            {
                if (Role == Role.Host)
                {
                    RemoteInput.ResetHost(); // Mugman lets go of every button while his player is away
                    Listen(openTransport());
                    Status = RemoteName + " lost connection — waiting for them to come back";
                }
                else
                {
                    Dial(openTransport());
                    Status = "Connection lost — reconnecting to " + RemoteName + " ...";
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not reopen the connection: " + ex.Message);
                Stop("connection timed out");
            }
        }

        /// <summary>
        /// Unity stops updating an unfocused window unless runInBackground is set, which would freeze the partner's
        /// game (and time the session out) whenever this player alt-tabs. Keep running while a session exists, and
        /// put the game's own setting back afterwards so offline play pauses on alt-tab as usual.
        /// </summary>
        private static void KeepRunningInBackground(bool keep)
        {
            if (keep)
            {
                if (runInBackgroundBefore == null) runInBackgroundBefore = Application.runInBackground;
                Application.runInBackground = true;
            }
            else if (runInBackgroundBefore != null)
            {
                Application.runInBackground = runInBackgroundBefore.Value;
                runInBackgroundBefore = null;
            }
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
            Reconnecting = false;
            openTransport = null;
            acceptedNonce = 0;
            if (reason != "restart") KeepRunningInBackground(false);
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
                LoseConnection("no packets for a while");
                return;
            }
            if (Reconnecting && State != LinkState.Connected && Time.realtimeSinceStartup > reconnectDeadline)
            {
                Stop(Role == Role.Host ? RemoteName + " did not come back" : "lost the connection to the host");
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
                if (!Reconnecting && now - connectStarted > 20.0)
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
            w.U32(helloNonce); // newer field: hosts before 0.1.3 stop reading after the game version
            Connection.SendReliable(MsgType.Hello, w);
        }

        private static void HostOnMessage(MsgType type, NetReader r)
        {
            switch (type)
            {
                case MsgType.Hello:
                {
                    byte version = r.U8();
                    string name = r.Str();
                    string gameVersion = r.Str();
                    uint nonce = r.Remaining >= 4 ? r.U32() : 0;
                    if (State == LinkState.Connected)
                    {
                        // A retransmit carries the same nonce. A new one means the player lost us and redialled
                        // before we noticed (Steam P2P keeps delivering from the same SteamID): take them back.
                        if (nonce != 0 && nonce != acceptedNonce) LoseConnection(RemoteName + " redialled");
                        return;
                    }
                    if (version != Protocol.Version || gameVersion != Application.version)
                    {
                        NetWriter reject = new NetWriter(64);
                        reject.Str("Version mismatch (host mod v" + Protocol.Version + ", game " + Application.version + ")");
                        Connection.SendReliable(MsgType.Reject, reject);
                        // The rejected peer is pinned by the transport; end this hosting session after sending.
                        pendingStop = "rejected a player with a different mod/game version (they have " + gameVersion + ")";
                        return;
                    }
                    bool rejoined = Reconnecting;
                    RemoteName = string.IsNullOrEmpty(name) ? "Player 2" : name;
                    State = LinkState.Connected;
                    Reconnecting = false;
                    acceptedNonce = nonce;
                    Status = rejoined ? RemoteName + " reconnected" : RemoteName + " joined — press any button on their side to spawn Mugman";
                    NetWriter welcome = new NetWriter(64);
                    welcome.Str(localName);
                    Connection.SendReliable(MsgType.Welcome, welcome);
                    HostSync.OnClientJoined(Connection);
                    SteamSupport.RememberPartner(Connection.Transport);
                    if (!rejoined) SaveBackups.BackUp();
                    Plugin.Log.LogInfo("Client " + (rejoined ? "rejoined: " : "joined: ") + RemoteName + " from " + Connection.Transport.PeerDescription);
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
                {
                    if (State == LinkState.Connected) return;
                    bool rejoined = Reconnecting && ClientMirror.Mirroring;
                    RemoteName = r.Str();
                    State = LinkState.Connected;
                    Reconnecting = false;
                    Status = (rejoined ? "Reconnected to " : "Connected to ") + RemoteName;
                    if (rejoined) ClientMirror.OnReconnected();
                    else ClientMirror.OnConnected();
                    SteamSupport.RememberPartner(Connection.Transport);
                    if (!rejoined) SaveBackups.BackUp();
                    Plugin.Log.LogInfo(Status);
                    break;
                }
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
