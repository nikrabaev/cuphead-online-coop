using System;
using System.Collections.Generic;
using Steamworks;

namespace CupheadOnline.Net
{
    /// <summary>
    /// Steam convenience layer: friends-only lobby for overlay invites, rich presence so friends can see who is
    /// hosting, and a list of hosting friends to join with one click. Steamworks.NET ships with the game
    /// (Assembly-CSharp-firstpass) and SteamManager already pumps SteamAPI.RunCallbacks.
    /// </summary>
    public static class SteamLobby
    {
        private const string PresenceKey = "cupheadonline";

        private static CallResult<LobbyCreated_t> lobbyCreated;
        private static Callback<GameLobbyJoinRequested_t> joinRequested;
        private static Callback<LobbyEnter_t> lobbyEntered;
        private static CSteamID lobby = CSteamID.Nil;
        private static bool installed;

        public static bool HasLobby { get { return lobby != CSteamID.Nil; } }

        public static void Install()
        {
            if (installed || !SteamTransport.Available) return;
            try
            {
                lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
                lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
                installed = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Steam lobby support unavailable: " + ex.Message);
            }
        }

        public static void HostViaSteam()
        {
            Install();
            Session.StartHost(SteamTransport.Host(), Plugin.PlayerName.Value);
            SteamFriends.SetRichPresence(PresenceKey, "host");
            SteamFriends.SetRichPresence("status", "Hosting online co-op");
            if (installed && !HasLobby)
            {
                SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
                lobbyCreated.Set(call);
            }
        }

        public static void JoinViaSteam(ulong hostId)
        {
            Session.StartClient(SteamTransport.Connect(hostId), Plugin.PlayerName.Value);
        }

        public static void InviteFriend()
        {
            if (HasLobby) SteamFriends.ActivateGameOverlayInviteDialog(lobby);
        }

        public static void EndHosting()
        {
            try
            {
                SteamFriends.SetRichPresence(PresenceKey, "");
                SteamFriends.SetRichPresence("status", "");
                if (HasLobby) SteamMatchmaking.LeaveLobby(lobby);
            }
            catch { }
            lobby = CSteamID.Nil;
        }

        public static List<SteamSupport.FriendInfo> HostingFriends()
        {
            List<SteamSupport.FriendInfo> list = new List<SteamSupport.FriendInfo>();
            if (!SteamTransport.Available) return list;
            AppId_t app = SteamUtils.GetAppID();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                CSteamID friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                FriendGameInfo_t info;
                if (!SteamFriends.GetFriendGamePlayed(friend, out info)) continue;
                if (info.m_gameID.AppID() != app) continue;
                if (SteamFriends.GetFriendRichPresence(friend, PresenceKey) != "host") continue;
                list.Add(new SteamSupport.FriendInfo { Id = friend.m_SteamID, Name = SteamFriends.GetFriendPersonaName(friend) });
            }
            return list;
        }

        private static void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogWarning("Steam lobby creation failed: " + result.m_eResult);
                return;
            }
            lobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(lobby, PresenceKey, Plugin.Version);
            Plugin.Log.LogInfo("Steam lobby ready; use 'Invite friend' in the online menu.");
        }

        private static void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            Plugin.Log.LogInfo("Accepted Steam invite from " + SteamFriends.GetFriendPersonaName(request.m_steamIDFriend));
            SteamMatchmaking.JoinLobby(request.m_steamIDLobby);
        }

        private static void OnLobbyEntered(LobbyEnter_t entered)
        {
            CSteamID entry = new CSteamID(entered.m_ulSteamIDLobby);
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(entry);
            if (owner == SteamUser.GetSteamID()) return; // our own hosting lobby
            if (Session.Role != Role.None) return;
            lobby = entry;
            Plugin.Log.LogInfo("Joining host " + SteamFriends.GetFriendPersonaName(owner) + " from lobby");
            JoinViaSteam(owner.m_SteamID);
        }
    }
}
