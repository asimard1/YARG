using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Converts the server's <see cref="LobbyDto"/> wire format into the
    /// browser's local <see cref="LobbyData"/> view model.
    /// </summary>
    internal static class LobbyMapper
    {
        public static LobbyData FromDto(LobbyDto dto) => new()
        {
            LobbyId         = dto.Id,
            LobbyName       = dto.Name,
            HostUserId      = dto.HostUserId,
            HostName        = dto.HostName,
            SongName        = dto.Song,
            PlayerCount     = dto.PlayerCount,
            PlayerMax       = dto.MaxPlayers,
            GameMode        = dto.GameMode,
            Region          = dto.Region,
            Status          = dto.Status,
            SharedSongCount = dto.SharedSongCount,
            CreatedAt       = dto.CreatedAt,
        };
    }
}
