using System;
using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.UI
{
    /// <summary>IMGUI menu (F8). Uses UnityEngine.Input/IMGUI, so it keeps working while Rewired input is blocked.</summary>
    public static class Overlay
    {
        public static bool Visible = true;
        private static Rect window = new Rect(40, 40, 460, 10);
        private static string address;
        private static string steamId;
        private static string portText;
        private static string error;
        private static List<SteamSupport.FriendInfo> friends = new List<SteamSupport.FriendInfo>();
        private static float lastFriendScan = -10f;
        private static float hintUntil = 12f;
        private static long lastBytesSent, lastBytesReceived;
        private static float lastRateSample;
        private static float upKbps, downKbps;

        public static void Draw()
        {
            if (address == null)
            {
                address = Plugin.LastAddress.Value;
                steamId = Plugin.LastSteamId.Value;
                portText = Plugin.Port.Value.ToString();
                Visible = false;
            }
            SampleRates();
            DrawBadge();
            if (!Visible) return;
            window = GUILayout.Window(0x0C0FFEE, window, DrawWindow, Plugin.DisplayName + " " + Plugin.Version + "   (" + Plugin.MenuKey.Value + " to close)");
        }

        private static void SampleRates()
        {
            Connection c = Session.Connection;
            if (c == null) { upKbps = downKbps = 0; return; }
            float now = Time.unscaledTime;
            if (now - lastRateSample < 1f) return;
            float dt = now - lastRateSample;
            if (lastRateSample > 0 && dt > 0)
            {
                upKbps = (c.BytesSent - lastBytesSent) * 8f / 1000f / dt;
                downKbps = (c.BytesReceived - lastBytesReceived) * 8f / 1000f / dt;
            }
            lastBytesSent = c.BytesSent;
            lastBytesReceived = c.BytesReceived;
            lastRateSample = now;
        }

        private static void DrawBadge()
        {
            string text;
            if (Session.State == LinkState.Connected)
            {
                text = (Session.IsHost ? "HOST" : "CLIENT") + " · " + Session.RemoteName + " · " + Mathf.RoundToInt((float)Session.Connection.RttSeconds * 1000f) + " ms";
            }
            else if (Session.Role != Role.None)
            {
                text = Session.Status;
            }
            else if (Time.unscaledTime < hintUntil)
            {
                text = "Online co-op: press " + Plugin.MenuKey.Value + " to host or join";
            }
            else return;
            if (badgeStyle == null) badgeStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            GUIStyle style = badgeStyle;
            Vector2 size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(8, Screen.height - size.y - 8, size.x + 8, size.y), text, style);
        }

        private static void DrawWindow(int id)
        {
            GUILayout.Label("Status: " + Session.Status);
            if (!string.IsNullOrEmpty(error)) GUILayout.Label("<color=#ff8080>" + error + "</color>", Rich());

            if (Session.Role != Role.None)
            {
                Connection c = Session.Connection;
                if (c != null)
                {
                    GUILayout.Label(string.Format("Peer: {0}   RTT: {1:0} ms   Up: {2:0} kbit/s   Down: {3:0} kbit/s",
                        c.Transport.PeerDescription, c.RttSeconds * 1000, upKbps, downKbps));
                }
                if (Session.Role == Role.Host && SteamSupport.HasLobby && GUILayout.Button("Invite a Steam friend…"))
                {
                    SteamSupport.InviteFriend();
                }
                if (Session.Role == Role.Host)
                {
                    GUILayout.Label("You play Cuphead (player 1). Your friend controls Mugman (player 2):\nthey press any button once you are on the map or in a level.");
                }
                else
                {
                    GUILayout.Label("You are player 2. Use your keyboard/controller as usual;\nthe host's game runs the action and streams it to you.");
                }
                if (GUILayout.Button(Session.Role == Role.Host ? "Stop hosting" : "Disconnect"))
                {
                    Session.Stop("you disconnected");
                    SteamSupport.EndHosting();
                }
                GUI.DragWindow();
                return;
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Host</b> — you keep your save; your friend joins as Mugman.", Rich());
            GUILayout.BeginHorizontal();
            if (SteamSupport.Available && GUILayout.Button("Host via Steam (no port forwarding)", GUILayout.Height(28)))
            {
                Guard(SteamSupport.HostViaSteam);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("UDP port", GUILayout.Width(70));
            portText = GUILayout.TextField(portText, GUILayout.Width(70));
            if (GUILayout.Button("Host by IP"))
            {
                Guard(() =>
                {
                    int port;
                    if (!int.TryParse(portText, out port) || port <= 0 || port > 65535) throw new Exception("Invalid port");
                    Plugin.Port.Value = port;
                    Session.StartHost(UdpTransport.Host(port), Plugin.PlayerName.Value);
                });
            }
            GUILayout.EndHorizontal();
            if (SteamSupport.Available)
            {
                GUILayout.Label("Your SteamID (for friends joining by ID): " + SteamSupport.LocalId);
            }

            GUILayout.Space(8);
            GUILayout.Label("<b>Join</b> — your screen mirrors the host's game.", Rich());
            if (!PlayerData.Initialized)
            {
                GUILayout.Label("<color=#ffd080>Tip: pick any save slot first so the game finishes loading.</color>", Rich());
            }
            if (SteamSupport.Available)
            {
                if (Time.unscaledTime - lastFriendScan > 2f)
                {
                    lastFriendScan = Time.unscaledTime;
                    try { friends = SteamSupport.HostingFriends(); } catch { friends.Clear(); }
                }
                if (friends.Count == 0) GUILayout.Label("No Steam friends are hosting right now.");
                foreach (SteamSupport.FriendInfo f in friends)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(f.Name);
                    if (GUILayout.Button("Join", GUILayout.Width(80)))
                    {
                        ulong hostId = f.Id;
                        Guard(() => SteamSupport.JoinViaSteam(hostId));
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("SteamID", GUILayout.Width(70));
                steamId = GUILayout.TextField(steamId);
                if (GUILayout.Button("Join", GUILayout.Width(80)))
                {
                    Guard(() =>
                    {
                        ulong parsed;
                        if (!ulong.TryParse(steamId.Trim(), out parsed)) throw new Exception("SteamID must be a 17-digit number");
                        Plugin.LastSteamId.Value = steamId.Trim();
                        SteamSupport.JoinViaSteam(parsed);
                    });
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("IP:port", GUILayout.Width(70));
            address = GUILayout.TextField(address);
            if (GUILayout.Button("Join", GUILayout.Width(80)))
            {
                Guard(() =>
                {
                    string host = address.Trim();
                    int port = Protocol.DefaultPort;
                    int colon = host.LastIndexOf(':');
                    if (colon > 0)
                    {
                        if (!int.TryParse(host.Substring(colon + 1), out port)) throw new Exception("Invalid port in address");
                        host = host.Substring(0, colon);
                    }
                    Plugin.LastAddress.Value = address.Trim();
                    Session.StartClient(UdpTransport.Connect(host, port), Plugin.PlayerName.Value);
                });
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name", GUILayout.Width(70));
            Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, 24);
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }

        private static GUIStyle rich;
        private static GUIStyle badgeStyle;
        private static GUIStyle Rich()
        {
            if (rich == null) rich = new GUIStyle(GUI.skin.label) { richText = true };
            return rich;
        }

        private static void Guard(Action action)
        {
            error = null;
            try { action(); }
            catch (Exception ex)
            {
                error = ex.Message;
                Plugin.Log.LogError(ex);
            }
        }
    }
}
