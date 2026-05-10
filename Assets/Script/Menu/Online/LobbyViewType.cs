using YARG.Menu.ListMenu;

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

        // Primary text: the song the lobby is currently on.
        public override string GetPrimaryText(bool selected)
        {
            return FormatAs(_lobby.SongName, TextType.Bright, selected);
        }

        // Secondary text: host • players (3/4) • type • state [• Private]
        public override string GetSecondaryText(bool selected)
        {
            string playerCount = $"{_lobby.PlayerCount}/{_lobby.PlayerMax}";
            string type        = _lobby.Type  == LobbyType.Hardcore  ? "Hardcore" : "Casual";
            string state       = _lobby.State == LobbyState.Playing  ? "Playing"  : "Song Select";

            string line = $"{_lobby.HostName}  •  {playerCount}  •  {type}  •  {state}";
            if (_lobby.IsPrivate) line += "  •  Private";

            return FormatAs(line, TextType.Secondary, selected);
        }

        public void PrimaryButtonClick()
        {
            _menu.JoinLobby(_lobby);
        }
    }
}
