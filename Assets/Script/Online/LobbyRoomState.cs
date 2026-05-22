using System;
using System.Collections.Generic;
using YARG.Core.Song;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Player;
using YARG.Song;
using Instrument = YARG.Core.Instrument;

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

        // userId -> currently-selected instrument. Populated for whoever the server
        // has sent us instrument data on; absent entries fall back to "unknown".
        // The local user is always seeded from PlayerContainer in FromCreate/FromEnter.
        public Dictionary<string, Instrument> MemberInstruments = new();

        // userId -> "is back in the lobby/song-select screen". True by default
        // (on join + on song-select). Flipped to false for every member when
        // the host starts a song. Each member flips back to true individually
        // when they signal LeaveResults from the post-game results screen.
        // The host's Start button is gated on every member's flag being true.
        public Dictionary<string, bool> MemberIsBackInLobby = new();

        /// <summary>True iff every member in <see cref="Members"/> has reported
        /// back to the lobby (i.e. nobody is still mid-game / on the results
        /// screen). Used by <c>LobbyViewMenu</c> to enable the host's Start
        /// button. Missing entries are treated as <c>true</c> (a member we
        /// haven't received a flag for yet is assumed to be in the lobby —
        /// the server still gates StartGame so a stale optimistic-true here
        /// is harmless).</summary>
        public bool AllMembersBackInLobby
        {
            get
            {
                foreach (var userId in Members)
                {
                    if (MemberIsBackInLobby.TryGetValue(userId, out var ready) && !ready) return false;
                }
                return true;
            }
        }

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
                state.MemberIsBackInLobby[session.LocalUserId] = true;
                if (PlayerContainer.Players.Count > 0)
                {
                    state.MemberInstruments[session.LocalUserId] =
                        PlayerContainer.Players[0].Profile.CurrentInstrument;
                }
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
                    state.MemberInstruments[member.UserId] = (Instrument) member.Instrument;
                    state.MemberIsBackInLobby[member.UserId] = member.IsBackInLobby;
                }
            }
            // Make sure the local row reflects the freshest PlayerContainer value (server may
            // not echo the just-sent value before the result fully serializes back).
            var session = LobbyHubSession.Current;
            if (session != null && !string.IsNullOrEmpty(session.LocalUserId)
                && PlayerContainer.Players.Count > 0)
            {
                state.MemberInstruments[session.LocalUserId] =
                    PlayerContainer.Players[0].Profile.CurrentInstrument;
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
