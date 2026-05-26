using TMPro;
using UnityEngine;
using YARG.Menu.ListMenu;

namespace YARG.Menu.Online
{
    /// <summary>
    /// MonoBehaviour on the LobbyView prefab -- equivalent of <c>SongView</c>
    /// for the lobby browser. Extends the default <see cref="ViewObject{T}"/>
    /// behavior (primary/secondary text, background swap on selected) with
    /// two extra TMP slots for the lobby type and lobby state badges.
    /// </summary>
    public class LobbyView : ViewObject<LobbyViewType>
    {
        [SerializeField] private TextMeshProUGUI _hostNameText;
        [SerializeField] private TextMeshProUGUI _lobbyTypeText;
        [SerializeField] private TextMeshProUGUI _lobbyStateText;

        public override void Show(bool selected, LobbyViewType viewType)
        {
            base.Show(selected, viewType);
            if (_hostNameText != null) _hostNameText.text = viewType.GetHostNameText();
            _lobbyTypeText.text  = viewType.GetLobbyTypeText();
            _lobbyStateText.text = viewType.GetLobbyStateText();
        }

        public void PrimaryButtonClick()
        {
            if (!Showing) return;
            ViewType.PrimaryButtonClick();
        }
    }
}
