using System.Collections.Generic;
using YARG.Online.Lobbies.Contracts.Enums;

namespace YARG.Menu.Online
{
    public enum LobbySortAttribute
    {
        SongName,
        HostName,
    }

    /// <summary>
    /// Stripped-down filter set for the lobby browser. Each field is independently
    /// toggleable; the browser shows the lobbies that satisfy ALL active rules.
    /// </summary>
    public sealed class LobbyFilterSettings
    {
        public bool         ShowFullLobbies = true;
        public GameMode?    OnlyGameMode    = null;   // null = any mode
        public LobbyStatus? OnlyStatus      = null;   // null = any status

        public bool Passes(LobbyData lobby)
        {
            if (!ShowFullLobbies     && lobby.IsFull)                       return false;
            if (OnlyGameMode.HasValue && lobby.GameMode != OnlyGameMode)    return false;
            if (OnlyStatus.HasValue   && lobby.Status   != OnlyStatus)      return false;
            return true;
        }
    }

    public static class LobbySorter
    {
        public static IEnumerable<LobbyData> Sort(IEnumerable<LobbyData> lobbies, LobbySortAttribute attribute)
        {
            return attribute switch
            {
                LobbySortAttribute.SongName => Order(lobbies, l => l.SongName),
                LobbySortAttribute.HostName => Order(lobbies, l => l.HostName),
                _                           => lobbies,
            };
        }

        private static IEnumerable<LobbyData> Order<TKey>(IEnumerable<LobbyData> lobbies, System.Func<LobbyData, TKey> key)
        {
            var list = new List<LobbyData>(lobbies);
            list.Sort((a, b) => Comparer<TKey>.Default.Compare(key(a), key(b)));
            return list;
        }
    }
}
