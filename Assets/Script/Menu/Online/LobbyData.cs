using System;
using YARG.Online.Lobbies.Contracts.Enums;

namespace YARG.Menu.Online
{
    /// <summary>
    /// In-memory lobby record backing one row of the browser. Mirrors
    /// <see cref="YARG.Online.Lobbies.Contracts.Rest.LobbyDto"/> with the
    /// fields the UI cares about.
    /// </summary>
    public sealed class LobbyData
    {
        public string         LobbyId         { get; set; }
        public string         LobbyName       { get; set; }
        public string         HostUserId      { get; set; }
        public string         HostName        { get; set; }
        public string         SongName        { get; set; }
        public int            PlayerCount     { get; set; }
        public int            PlayerMax       { get; set; }
        public GameMode       GameMode        { get; set; }
        public Region         Region          { get; set; }
        public LobbyStatus    Status          { get; set; }
        public int            SharedSongCount { get; set; }
        public DateTimeOffset CreatedAt       { get; set; }

        public bool IsFull => PlayerCount >= PlayerMax;
    }
}
