using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace CupheadOnline.Net
{
    /// <summary>
    /// The only entry point other code uses for Steam features. Copies of Cuphead without Steamworks (GOG,
    /// Microsoft Store) must still load the mod and play over IP, so nothing here mentions a Steamworks type
    /// directly: each call checks <see cref="Present"/> and then jumps into a NoInlining method, so the JIT
    /// only resolves SteamLobby/SteamTransport (and their Steamworks types) when Steam actually exists.
    /// </summary>
    public static class SteamSupport
    {
        public struct FriendInfo
        {
            public ulong Id;
            public string Name;
        }

        private static int present = -1;

        /// <summary>Steamworks.NET and Cuphead's SteamManager are part of this game build.</summary>
        public static bool Present
        {
            get
            {
                if (present < 0)
                {
                    bool ok;
                    try { ok = AccessTools.TypeByName("Steamworks.SteamAPI") != null && AccessTools.TypeByName("SteamManager") != null; }
                    catch { ok = false; }
                    present = ok ? 1 : 0;
                    if (!ok) Plugin.Log.LogInfo("Steamworks not found in this game build: Steam invites are disabled, play over IP.");
                }
                return present == 1;
            }
        }

        /// <summary>Steam is present and running with a signed-in user.</summary>
        public static bool Available { get { return Present && Call(AvailableCore, false); } }

        public static bool HasLobby { get { return Present && Call(HasLobbyCore, false); } }
        public static ulong LocalId { get { return Present ? Call(LocalIdCore, 0UL) : 0UL; } }

        public static void Install() { if (Present) Call(InstallCore); }
        public static void HostViaSteam() { Require(); HostCore(); }
        public static void JoinViaSteam(ulong hostId) { Require(); JoinCore(hostId); }
        public static void InviteFriend() { if (Present) Call(InviteCore); }
        public static void EndHosting() { if (Present) Call(EndHostingCore); }

        public static List<FriendInfo> HostingFriends()
        {
            return Present ? Call(FriendsCore, new List<FriendInfo>()) : new List<FriendInfo>();
        }

        private static void Require()
        {
            if (!Available) throw new InvalidOperationException("Steam is not available in this copy of the game. Use Host/Join by IP.");
        }

        private static T Call<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug("Steam call failed: " + ex.Message);
                return fallback;
            }
        }

        private static void Call(Action a)
        {
            try { a(); }
            catch (Exception ex) { Plugin.Log.LogWarning("Steam call failed: " + ex.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static bool AvailableCore() { return SteamTransport.Available; }
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool HasLobbyCore() { return SteamLobby.HasLobby; }
        [MethodImpl(MethodImplOptions.NoInlining)] private static ulong LocalIdCore() { return SteamTransport.LocalId; }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void InstallCore() { SteamLobby.Install(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void HostCore() { SteamLobby.HostViaSteam(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void JoinCore(ulong id) { SteamLobby.JoinViaSteam(id); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void InviteCore() { SteamLobby.InviteFriend(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void EndHostingCore() { SteamLobby.EndHosting(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static List<FriendInfo> FriendsCore() { return SteamLobby.HostingFriends(); }
    }
}
