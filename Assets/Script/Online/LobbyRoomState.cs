using System;
using System.Collections.Generic;
using YARG.Core.Song;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// Mutable client-side model of the lobby the local player is currently in.
    /// Built from the <c>CreateLobby</c> result (host case) or the full
    /// <c>EnterLobby</c> result (joiner case), then kept up-to-date by the
    /// in-lobby callbacks on <see cref="LobbyHubSession"/>.
    /// </summary>
    public sealed class LobbyRoomState
    {
        public string      LobbyId;
        public string      LobbyName;
        public string      HostUserId;
        public string      HostName;
        public GameMode    GameMode;
        public Region      Region;
        public LobbyStatus Status;
        public int         MaxPlayers;

        // userIds of members currently in the lobby (includes the local player).
        public List<string>          Members          = new();
        public List<ChatMessage>     ChatHistory      = new();
        // The lobby's effective song library from the local player's perspective:
        // {songs the local player owns} ∩ {songs every member owns}.
        // MusicLibraryMenu.AllowedSongHashes aliases this set directly while in a lobby,
        // so mutations from LobbySongLibraryUpdatedEvent are visible to the filter
        // without a separate sync step.
        public HashSet<HashWrapper>  LobbySongLibrary = new();
        public List<QueuedSongDto>   SongQueue        = new();

        // userId -> displayName for every user we've seen in this lobby.
        // Seeded by FromCreate/FromEnter, kept fresh by OnPlayerJoined.
        // Entries are not removed when a member leaves so leave/kick toasts
        // can still resolve a name.
        public Dictionary<string, string> MemberNames = new();

        // Set by OnGameStarted; consumed by the future scene-handoff path.
        public string         GameServerEndpoint;
        public string         GameConnectionKey;
        public string         GameToken;
        public DateTimeOffset GameTokenExpiresAt;

        public bool IsLocalHost => HostUserId != null && HostUserId == LobbyHubSession.Current?.LocalUserId;

        public string GetDisplayName(string userId) =>
            MemberNames.TryGetValue(userId, out var name) ? name : userId;

        public static LobbyRoomState FromCreate(LobbyDto lobby)
        {
            var state = FromLobbyDto(lobby);
            // Creator is the only member at creation time; the server will
            // broadcast joins via OnPlayerJoined as others enter.
            var session = LobbyHubSession.Current;
            if (session != null && !string.IsNullOrEmpty(session.LocalUserId))
            {
                state.Members.Add(session.LocalUserId);
                state.MemberNames[session.LocalUserId] = session.LocalDisplayName;
            }

            // Host owns the entire initial lobby library — seed from the local song container.
            var localHashes = SongContainer.SongsByHash.Keys;
            state.LobbySongLibrary = new HashSet<HashWrapper>();
            foreach (var hash in localHashes)
                state.LobbySongLibrary.Add(hash);

            return state;
        }

        public static LobbyRoomState FromEnter(EnterLobbyResult result)
        {
            var state = FromLobbyDto(result.Lobby);
            if (result.CurrentMembers != null)
            {
                foreach (var member in result.CurrentMembers)
                {
                    state.Members.Add(member.UserId);
                    state.MemberNames[member.UserId] = member.DisplayName;
                }
            }
            if (result.ChatHistory != null) state.ChatHistory.AddRange(result.ChatHistory);
            if (result.SongQueue   != null) state.SongQueue.AddRange(result.SongQueue);

            // Joiner's effective lobby library = local library ∖ server-reported removals.
            HashSet<HashWrapper> removalSet = null;
            if (result.LibraryRemovals is { Length: > 0 } removals)
            {
                removalSet = new HashSet<HashWrapper>(removals.Length);
                foreach (var s in removals)
                    removalSet.Add(HashWrapper.FromString(s.AsSpan()));
            }

            var localHashes = SongContainer.SongsByHash.Keys;
            state.LobbySongLibrary = new HashSet<HashWrapper>();
            foreach (var hash in localHashes)
            {
                if (removalSet != null && removalSet.Contains(hash)) continue;
                state.LobbySongLibrary.Add(hash);
            }

            return state;
        }

        private static LobbyRoomState FromLobbyDto(LobbyDto lobby) => new()
        {
            LobbyId    = lobby.Id,
            LobbyName  = lobby.Name,
            HostUserId = lobby.HostUserId,
            HostName   = lobby.HostName,
            GameMode   = lobby.GameMode,
            Region     = lobby.Region,
            Status     = lobby.Status,
            MaxPlayers = lobby.MaxPlayers,
        };
    }
}
