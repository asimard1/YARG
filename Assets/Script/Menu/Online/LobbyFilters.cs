using System.Collections.Generic;

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
        public bool       ShowFullLobbies    = true;
        public bool       ShowPrivateLobbies = true;
        public LobbyType? OnlyType           = null;   // null = any type
        public LobbyState? OnlyState         = null;   // null = any state

        public bool Passes(LobbyData lobby)
        {
            if (!ShowFullLobbies    && lobby.IsFull)              return false;
            if (!ShowPrivateLobbies && lobby.IsPrivate)           return false;
            if (OnlyType.HasValue   && lobby.Type  != OnlyType)   return false;
            if (OnlyState.HasValue  && lobby.State != OnlyState)  return false;
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
