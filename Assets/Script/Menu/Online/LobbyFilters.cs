using System.Collections.Generic;
using YARG.Online.Lobbies.Contracts.Enums;
// Alias the contract enums so unqualified references below resolve to the
// global YARG.Online.Lobbies.Contracts.Enums types — without these, the
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

    /// <summary>
    /// Filter set for the lobby browser, mirroring MusicLibrary's filter
    /// menu shape — each field is an independent boolean toggle rendered
    /// as a <c>FilterEntryRow</c> in the popup. Default state has every
    /// toggle on (= show everything); flipping one off filters out
    /// lobbies of that category. Multi-select inside category groups.
    /// </summary>
    public sealed class LobbyFilterSettings
    {
        public bool ShowFullLobbies = true;
        public bool LanOnly         = false; // unused placeholder — LobbyData has no IsLan flag yet

        // Game-mode visibility — both on by default. Turn one off to hide
        // that mode's lobbies. Both off hides everything mode-wise.
        public bool ShowBand        = true;
        public bool ShowQuickplay   = true;

        // Status visibility — same pattern as game mode.
        public bool ShowSongSelect  = true;
        public bool ShowInGame      = true;

        public bool Passes(LobbyData lobby)
        {
            if (!ShowFullLobbies && lobby.IsFull) return false;

            // LAN filter has no LobbyData hook yet — gate here when
            // LobbyData grows an IsLan field.

            if (!ShowBand      && lobby.GameMode == ContractGameMode.Band)      return false;
            if (!ShowQuickplay && lobby.GameMode == ContractGameMode.Quickplay) return false;

            if (!ShowSongSelect && lobby.Status == ContractStatus.SongSelect)  return false;
            if (!ShowInGame     && lobby.Status == ContractStatus.GameStarted) return false;

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
