using YARG.Menu.ListMenu;

namespace YARG.Menu.Online
{
    /// <summary>
    /// MonoBehaviour on the LobbyView prefab — equivalent of <c>SongView</c>
    /// for the lobby browser. Inherits the default <see cref="ViewObject{T}"/>
    /// behavior (primary/secondary text, background swap on selected). No
    /// stars / difficulty / favorites, so no overrides needed yet.
    /// </summary>
    public class LobbyView : ViewObject<LobbyViewType>
    {
        public void PrimaryButtonClick()
        {
            if (!Showing) return;
            ViewType.PrimaryButtonClick();
        }
    }
}
