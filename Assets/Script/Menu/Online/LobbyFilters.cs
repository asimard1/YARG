using System.Collections.Generic;
using YARG.Online.Lobbies.Contracts.Enums;
// Alias the contract enums so unqualified references below resolve to the
// global YARG.Online.Lobbies.Contracts.Enums types -- without these, the
// `Online.Lobbies.*` paths in the switch arms get hijacked by the enclosing
// YARG.Menu.Online namespace and fail to compile.
using ContractGameMode = YARG.Online.Lobbies.Contracts.Enums.GameMode;
using ContractStatus   = YARG.Online.Lobbies.Contracts.Enums.LobbyStatus;

namespace YARG.Menu.Online
{
    public enum LobbySortAttribute
    {
        LobbyName,
        HostName,
        SongName,
        PlayerCount,
    }

    // UI-side filter enums with explicit "Any" sentinels so dropdown bindings
    // can carry a single value (matches DropdownSetting<T>'s shape -- nullables
    // don't play well with the IndexOf/Equals machinery in DropdownSetting).
    public enum LobbyGameModeFilter
    {
        Any,
        Band,
        Versus, // wire-side: ContractGameMode.Quickplay
    }

    public enum LobbyStatusFilter
    {
        Any,
        SongSelect,
        InGame, // covers both Starting and GameStarted
    }

    public sealed class LobbyFilterSettings
    {
        public bool ShowFullLobbies = true;
        public bool LanOnly         = false; // unused placeholder -- LobbyData has no IsLan flag yet

        public LobbyGameModeFilter GameModeFilter = LobbyGameModeFilter.Any;
        public LobbyStatusFilter   StatusFilter   = LobbyStatusFilter.Any;

        public bool Passes(LobbyData lobby)
        {
            if (!ShowFullLobbies && lobby.IsFull) return false;

            // LAN filter has no LobbyData hook yet -- gate here when
            // LobbyData grows an IsLan field.

            switch (GameModeFilter)
            {
                case LobbyGameModeFilter.Band:
                    if (lobby.GameMode != ContractGameMode.Band) return false;
                    break;
                case LobbyGameModeFilter.Versus:
                    if (lobby.GameMode != ContractGameMode.Quickplay) return false;
                    break;
            }

            // Status filter rolls Starting in with GameStarted under "In-game" -- both are
            // post-song-select transient states users think of as "in a game".
            switch (StatusFilter)
            {
                case LobbyStatusFilter.SongSelect:
                    if (lobby.Status != ContractStatus.SongSelect) return false;
                    break;
                case LobbyStatusFilter.InGame:
                    if (lobby.Status == ContractStatus.SongSelect) return false;
                    break;
            }

            return true;
        }
    }

    public static class LobbySorter
    {
        public static IEnumerable<LobbyData> Sort(IEnumerable<LobbyData> lobbies, LobbySortAttribute attribute)
        {
            return attribute switch
            {
                LobbySortAttribute.LobbyName   => Order(lobbies, l => l.LobbyName),
                LobbySortAttribute.HostName    => Order(lobbies, l => l.HostName),
                LobbySortAttribute.SongName    => Order(lobbies, l => l.SongName),
                // Player count sorts high → low so popular lobbies surface
                // at the top. The other three (string-keyed) sort A → Z.
                LobbySortAttribute.PlayerCount => OrderDescending(lobbies, l => l.PlayerCount),
                _                              => lobbies,
            };
        }

        private static IEnumerable<LobbyData> Order<TKey>(IEnumerable<LobbyData> lobbies, System.Func<LobbyData, TKey> key)
        {
            var list = new List<LobbyData>(lobbies);
            list.Sort((a, b) => Comparer<TKey>.Default.Compare(key(a), key(b)));
            return list;
        }

        private static IEnumerable<LobbyData> OrderDescending<TKey>(IEnumerable<LobbyData> lobbies, System.Func<LobbyData, TKey> key)
        {
            var list = new List<LobbyData>(lobbies);
            list.Sort((a, b) => Comparer<TKey>.Default.Compare(key(b), key(a)));
            return list;
        }
    }
}
