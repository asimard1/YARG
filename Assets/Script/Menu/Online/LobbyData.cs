using System.Collections.Generic;

namespace YARG.Menu.Online
{
    public enum LobbyType
    {
        Casual,
        Hardcore,
    }

    public enum LobbyState
    {
        SongSelect,
        Playing,
    }

    /// <summary>
    /// In-memory lobby record. While the netcode isn't wired up yet, the
    /// browser is filled with dummy <see cref="LobbyData"/> via
    /// <see cref="LobbyData.GenerateDummies"/>; once we have real net data we
    /// just swap the source.
    /// </summary>
    public sealed class LobbyData
    {
        public string     SongName  { get; set; }
        public string     HostName  { get; set; }
        public int        PlayerCount  { get; set; }
        public int        PlayerMax { get; set; }
        public LobbyType  Type      { get; set; }
        public LobbyState State     { get; set; }
        public bool       IsPrivate { get; set; }

        public bool IsFull => PlayerCount >= PlayerMax;

        public static List<LobbyData> GenerateDummies()
        {
            // Roughly representative spread of states/types for testing the
            // sort/filter UI. Mix of full/non-full and private/public.
            return new List<LobbyData>
            {
                new() { SongName = "Through the Fire and Flames", HostName = "DragonSlayer", PlayerCount = 3, PlayerMax = 4, Type = LobbyType.Hardcore, State = LobbyState.Playing,    IsPrivate = false },
                new() { SongName = "Free Bird",                   HostName = "skynyrd_fan", PlayerCount = 2, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.SongSelect, IsPrivate = false },
                new() { SongName = "Bohemian Rhapsody",           HostName = "QueenOfNothing", PlayerCount = 4, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.Playing,    IsPrivate = false },
                new() { SongName = "Cliffs of Dover",             HostName = "EricJ",       PlayerCount = 1, PlayerMax = 4, Type = LobbyType.Hardcore, State = LobbyState.SongSelect, IsPrivate = true  },
                new() { SongName = "Sweet Child O' Mine",         HostName = "axl_rose",    PlayerCount = 3, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.Playing,    IsPrivate = false },
                new() { SongName = "Fade to Black",               HostName = "Hetfield42",  PlayerCount = 2, PlayerMax = 4, Type = LobbyType.Hardcore, State = LobbyState.SongSelect, IsPrivate = false },
                new() { SongName = "Master of Puppets",           HostName = "PuppetMaster", PlayerCount = 4, PlayerMax = 4, Type = LobbyType.Hardcore, State = LobbyState.Playing,    IsPrivate = true  },
                new() { SongName = "Painkiller",                  HostName = "halford",     PlayerCount = 2, PlayerMax = 4, Type = LobbyType.Hardcore, State = LobbyState.SongSelect, IsPrivate = false },
                new() { SongName = "Crazy Train",                 HostName = "Ozzy",        PlayerCount = 1, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.SongSelect, IsPrivate = false },
                new() { SongName = "Smoke on the Water",          HostName = "deepP",       PlayerCount = 3, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.SongSelect, IsPrivate = false },
                new() { SongName = "Eruption",                    HostName = "EVH",         PlayerCount = 4, PlayerMax = 4, Type = LobbyType.Hardcore, State = LobbyState.Playing,    IsPrivate = false },
                new() { SongName = "Welcome to the Jungle",       HostName = "slash_69",    PlayerCount = 2, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.Playing,    IsPrivate = false },
                new() { SongName = "Highway to Hell",             HostName = "ACDCfan",     PlayerCount = 1, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.SongSelect, IsPrivate = true  },
                new() { SongName = "One",                         HostName = "Hetfield42",  PlayerCount = 3, PlayerMax = 4, Type = LobbyType.Hardcore, State = LobbyState.SongSelect, IsPrivate = false },
                new() { SongName = "Stairway to Heaven",          HostName = "ZepHead",     PlayerCount = 2, PlayerMax = 4, Type = LobbyType.Casual,   State = LobbyState.Playing,    IsPrivate = false },
            };
        }
    }
}
