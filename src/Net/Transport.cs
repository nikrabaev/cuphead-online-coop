using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace CupheadOnline.Net
{
    /// <summary>Unreliable datagram pipe to exactly one remote peer.</summary>
    public interface IDatagramTransport : IDisposable
    {
        /// <summary>True once a remote peer is known (client: immediately; host: after first packet).</summary>
        bool HasPeer { get; }
        string PeerDescription { get; }
        int MaxDatagramSize { get; }
        void Send(byte[] data, int length);
        /// <summary>Returns false when no datagram is pending. The buffer is owned by the transport until the next call.</summary>
        bool TryReceive(out byte[] data, out int length);
    }

    /// <summary>Plain UDP. The host binds a port and adopts the first peer that sends a valid hello.</summary>
    public sealed class UdpTransport : IDatagramTransport
    {
        private readonly Socket socket;
        private EndPoint peer;
        private readonly byte[] recvBuffer = new byte[2048];
        private readonly bool isHost;

        public bool HasPeer { get { return peer != null; } }
        public string PeerDescription { get { return peer == null ? "(waiting)" : peer.ToString(); } }
        public int MaxDatagramSize { get { return 1200; } }

        public static UdpTransport Host(int port)
        {
            return new UdpTransport(new IPEndPoint(IPAddress.Any, port), null, true);
        }

        public static UdpTransport Connect(string host, int port)
        {
            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                address = null;
                foreach (IPAddress candidate in Dns.GetHostAddresses(host))
                {
                    if (candidate.AddressFamily == AddressFamily.InterNetwork) { address = candidate; break; }
                }
                if (address == null) throw new Exception("Could not resolve " + host);
            }
            return new UdpTransport(new IPEndPoint(IPAddress.Any, 0), new IPEndPoint(address, port), false);
        }

        private UdpTransport(IPEndPoint bind, IPEndPoint remote, bool isHost)
        {
            this.isHost = isHost;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Blocking = false;
            // Windows-style ICMP "connection reset" is irrelevant on macOS, but keep the buffers roomy.
            socket.ReceiveBufferSize = 1 << 20;
            socket.SendBufferSize = 1 << 20;
            socket.Bind(bind);
            peer = remote;
        }

        public void Send(byte[] data, int length)
        {
            if (peer == null) return;
            try { socket.SendTo(data, 0, length, SocketFlags.None, peer); }
            catch (SocketException) { }
        }

        public bool TryReceive(out byte[] data, out int length)
        {
            data = recvBuffer;
            length = 0;
            while (true)
            {
                if (socket.Available <= 0) return false;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = socket.ReceiveFrom(recvBuffer, ref from); }
                catch (SocketException) { return false; }
                if (n <= 0) continue;
                if (peer == null)
                {
                    if (!isHost || !Protocol.IsHello(recvBuffer, n)) continue;
                    peer = from;
                }
                else if (!from.Equals(peer))
                {
                    continue; // only one remote player
                }
                length = n;
                return true;
            }
        }

        public void Dispose()
        {
            try { socket.Close(); } catch { }
        }
    }

    /// <summary>Steam P2P (ISteamNetworking) — NAT traversal and relay through Valve, no port forwarding needed.</summary>
    public sealed class SteamTransport : IDatagramTransport
    {
        private const int Channel = 7;
        private CSteamID peer;
        private bool hasPeer;
        private readonly bool isHost;
        private readonly byte[] recvBuffer = new byte[4096];
        private Callback<P2PSessionRequest_t> sessionRequest;

        public bool HasPeer { get { return hasPeer; } }
        public string PeerDescription
        {
            get
            {
                if (!hasPeer) return "(waiting)";
                string name = SteamFriends.GetFriendPersonaName(peer);
                return string.IsNullOrEmpty(name) ? peer.ToString() : name + " (" + peer + ")";
            }
        }
        public int MaxDatagramSize { get { return 1150; } }

        public static bool Available
        {
            get
            {
                // SteamManager (Assembly-CSharp) is internal; it owns SteamAPI.Init and RunCallbacks.
                try
                {
                    System.Reflection.PropertyInfo p = HarmonyLib.AccessTools.Property(HarmonyLib.AccessTools.TypeByName("SteamManager"), "Initialized");
                    return p != null && (bool)p.GetValue(null, null) && SteamUser.GetSteamID().IsValid();
                }
                catch { return false; }
            }
        }

        public static ulong LocalId { get { return SteamUser.GetSteamID().m_SteamID; } }

        public static SteamTransport Host()
        {
            return new SteamTransport(true, CSteamID.Nil);
        }

        public static SteamTransport Connect(ulong steamId)
        {
            return new SteamTransport(false, new CSteamID(steamId));
        }

        private SteamTransport(bool isHost, CSteamID remote)
        {
            this.isHost = isHost;
            SteamNetworking.AllowP2PPacketRelay(true);
            sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            if (!isHost)
            {
                peer = remote;
                hasPeer = true;
            }
        }

        private void OnSessionRequest(P2PSessionRequest_t request)
        {
            if (!isHost && request.m_steamIDRemote != peer) return;
            if (isHost && hasPeer && request.m_steamIDRemote != peer) return;
            SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
        }

        public void Send(byte[] data, int length)
        {
            if (!hasPeer) return;
            // Plain unreliable (not NoDelay): NoDelay packets are dropped until the P2P session is established.
            SteamNetworking.SendP2PPacket(peer, data, (uint)length, EP2PSend.k_EP2PSendUnreliable, Channel);
        }

        public bool TryReceive(out byte[] data, out int length)
        {
            data = recvBuffer;
            length = 0;
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, Channel))
            {
                if (size > recvBuffer.Length)
                {
                    // Drain oversized junk.
                    byte[] big = new byte[size];
                    CSteamID junkFrom;
                    SteamNetworking.ReadP2PPacket(big, size, out size, out junkFrom, Channel);
                    continue;
                }
                CSteamID from;
                if (!SteamNetworking.ReadP2PPacket(recvBuffer, (uint)recvBuffer.Length, out size, out from, Channel)) return false;
                if (!hasPeer)
                {
                    if (!isHost || !Protocol.IsHello(recvBuffer, (int)size)) continue;
                    peer = from;
                    hasPeer = true;
                }
                else if (from != peer)
                {
                    continue;
                }
                length = (int)size;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (hasPeer) SteamNetworking.CloseP2PSessionWithUser(peer);
            if (sessionRequest != null) sessionRequest.Unregister();
            sessionRequest = null;
        }
    }
}
