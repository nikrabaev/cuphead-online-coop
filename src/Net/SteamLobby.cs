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
        private static Callback<GameRichPresenceJoinRequested_t> presenceJoin;
        private static CSteamID lobby = CSteamID.Nil;
        private static ulong pendingInvite;
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
                presenceJoin = Callback<GameRichPresenceJoinRequested_t>.Create(OnPresenceJoin);
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
            Session.StartHost(SteamTransport.Host, Plugin.PlayerName.Value);
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
            Session.StartClient(() => SteamTransport.Connect(hostId), Plugin.PlayerName.Value);
        }

        /// <summary>Joins a host's lobby (from an invite or a friend's "Join Game"); OnLobbyEntered connects to its owner.</summary>
        public static void JoinLobby(ulong lobbyId)
        {
            Install();
            if (Session.Role != Role.None)
            {
                Plugin.Log.LogInfo("Ignoring a Steam invite: already in an online session");
                return;
            }
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
        }

        public static void InviteFriend()
        {
            if (HasLobby) SteamFriends.ActivateGameOverlayInviteDialog(lobby);
        }

        /// <summary>Starts hosting and sends this friend a Steam invite as soon as the lobby exists.</summary>
        public static void HostAndInvite(ulong friend)
        {
            HostViaSteam();
            if (HasLobby) Invite(friend);
            else pendingInvite = friend;
        }

        public static void Invite(ulong friend)
        {
            if (!HasLobby) return;
            bool sent = SteamMatchmaking.InviteUserToLobby(lobby, new CSteamID(friend));
            Plugin.Log.LogInfo((sent ? "Sent a Steam invite to " : "Could not invite ") + SteamFriends.GetFriendPersonaName(new CSteamID(friend)));
        }

        public static void RememberPartner(IDatagramTransport transport)
        {
            SteamTransport steam = transport as SteamTransport;
            if (steam == null || steam.PeerId == 0) return;
            Plugin.LastPartnerId.Value = steam.PeerId.ToString();
            Plugin.LastPartnerName.Value = SteamFriends.GetFriendPersonaName(new CSteamID(steam.PeerId));
        }

        public static SteamSupport.PartnerInfo Partner(ulong id)
        {
            CSteamID friend = new CSteamID(id);
            SteamSupport.PartnerInfo info = new SteamSupport.PartnerInfo { Id = id, Name = SteamFriends.GetFriendPersonaName(friend) };
            if (string.IsNullOrEmpty(info.Name) || info.Name == "[unknown]") info.Name = Plugin.LastPartnerName.Value;
            info.Online = SteamFriends.GetFriendPersonaState(friend) != EPersonaState.k_EPersonaStateOffline;
            FriendGameInfo_t game;
            info.InCuphead = SteamFriends.GetFriendGamePlayed(friend, out game) && game.m_gameID.AppID() == SteamUtils.GetAppID();
            info.Hosting = info.InCuphead && SteamFriends.GetFriendRichPresence(friend, PresenceKey) == "host";
            return info;
        }

        public static void EndHosting()
        {
            try
            {
                SteamFriends.SetRichPresence(PresenceKey, "");
                SteamFriends.SetRichPresence("status", "");
                SteamFriends.SetRichPresence("connect", "");
                if (HasLobby) SteamMatchmaking.LeaveLobby(lobby);
            }
            catch { }
            lobby = CSteamID.Nil;
            pendingInvite = 0;
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
            // Friends-list "Join Game": Steam hands this to our game (OnPresenceJoin), or launches Cuphead with it.
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + lobby.m_SteamID);
            Plugin.Log.LogInfo("Steam lobby ready; use 'Invite friend' in the online menu.");
            if (pendingInvite != 0) Invite(pendingInvite);
            pendingInvite = 0;
        }

        private static void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            Plugin.Log.LogInfo("Accepted Steam invite from " + SteamFriends.GetFriendPersonaName(request.m_steamIDFriend));
            JoinLobby(request.m_steamIDLobby.m_SteamID);
        }

        private static void OnPresenceJoin(GameRichPresenceJoinRequested_t request)
        {
            ulong lobbyId = SteamSupport.ParseConnectLobby(request.m_rgchConnect.Split(' '));
            Plugin.Log.LogInfo("Joining " + SteamFriends.GetFriendPersonaName(request.m_steamIDFriend) + " from the friends list");
            if (lobbyId != 0) JoinLobby(lobbyId);
        }

        private static void OnLobbyEntered(LobbyEnter_t entered)
        {
            CSteamID entry = new CSteamID(entered.m_ulSteamIDLobby);
            if (entered.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Log.LogWarning("Could not enter the host's Steam lobby (" + (EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse + "); it may have closed");
                return;
            }
            CSteamID owner = SteamMatchmaking.GetLobbyOwner(entry);
            if (!owner.IsValid()) return;
            if (owner == SteamUser.GetSteamID()) return; // our own hosting lobby
            if (Session.Role != Role.None) return;
            lobby = entry;
            Plugin.Log.LogInfo("Joining host " + SteamFriends.GetFriendPersonaName(owner) + " from lobby");
            JoinViaSteam(owner.m_SteamID);
        }
    }
}
