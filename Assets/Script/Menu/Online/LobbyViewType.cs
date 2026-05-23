using YARG.Menu.ListMenu;
using YARG.Online.Lobbies.Contracts.Enums;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Row data for one lobby in the browser list. Mirrors the role of
    /// <c>SongViewType</c> in the music library, but with lobby fields and
    /// no scoring/star concepts.
    /// </summary>
    public sealed class LobbyViewType : BaseViewType
    {
        private readonly LobbyData   _lobby;
        private readonly OnlineMenu  _menu;

        public LobbyData Lobby => _lobby;

        public LobbyViewType(OnlineMenu menu, LobbyData lobby)
        {
            _menu  = menu;
            _lobby = lobby;
        }

        public override BackgroundType Background => BackgroundType.Normal;

        public override string GetPrimaryText(bool selected)
        {
            return $"{_lobby.HostName} | {_lobby.LobbyName}";
        }

        public override string GetSecondaryText(bool selected)
        {
            return $"{_lobby.PlayerCount}/{_lobby.PlayerMax} Players";
        }

        public string GetLobbyTypeText()
        {
            return _lobby.GameMode switch
            {
                GameMode.Band      => "Band",
                // Contract enum value is "Quickplay" (kept for wire compat);
                // user-visible name is "Versus".
                GameMode.Quickplay => "Versus",
                _                  => _lobby.GameMode.ToString(),
            };
        }

        public string GetLobbyStateText()
        {
            return _lobby.Status switch
            {
                LobbyStatus.GameStarted => "Playing",
                LobbyStatus.SongSelect  => "Song Select",
                _                       => _lobby.Status.ToString(),
            };
        }

        public void PrimaryButtonClick()
        {
            _menu.JoinLobby(_lobby);
        }
    }
}
