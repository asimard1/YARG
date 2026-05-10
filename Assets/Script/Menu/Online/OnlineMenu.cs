using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Menu.ListMenu;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Lobby browser. Modeled after <c>MusicLibraryMenu</c> but stripped down
    /// to just rows-of-lobbies with sort/filter — no playlists, recommendations,
    /// shows, previews, scoring, stars or difficulty.
    /// </summary>
    public class OnlineMenu : ListMenu<LobbyViewType, LobbyView>
    {
        protected override int ExtraListViewPadding => 6;

        // Master list of (currently dummy) lobbies. Replace with a network feed
        // once the netcode is ready.
        private List<LobbyData> _allLobbies;

        // Active sort/filter state. Survives only for the lifetime of the
        // browser; persist via SettingsManager later if needed.
        private LobbySortAttribute  _sortAttribute = LobbySortAttribute.SongName;
        private LobbyFilterSettings _filters       = new();

        public LobbySortAttribute  SortAttribute { get => _sortAttribute; set { _sortAttribute = value; RequestViewListUpdate(); } }
        public LobbyFilterSettings Filters       => _filters;

        protected override void Awake()
        {
            base.Awake();
            _allLobbies = LobbyData.GenerateDummies();
            RequestViewListUpdate();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            PushNavigationScheme();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        private void PushNavigationScheme()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Up,    "Menu.Common.Up",      _ => { SetWrapAroundState(true); SelectedIndex--; }),
                new NavigationScheme.Entry(MenuAction.Down,  "Menu.Common.Down",    _ => { SetWrapAroundState(true); SelectedIndex++; }),
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", () => CurrentSelection?.PrimaryButtonClick(), hide: true),
                new NavigationScheme.Entry(MenuAction.Red,   "Menu.Common.Back",    Back, hide: true),

                // Bottom-row action buttons (UI matches Y/B/O button colors).
                new NavigationScheme.Entry(MenuAction.Yellow, "Menu.Online.CreateLobby",  CreateLobby),
                new NavigationScheme.Entry(MenuAction.Blue,   "Menu.Online.Filters",      OpenFilters),
                new NavigationScheme.Entry(MenuAction.Orange, "Menu.Online.JoinByCode",   JoinByCode),
            }, false));
        }

        protected override List<LobbyViewType> CreateViewList()
        {
            var list = new List<LobbyViewType>();
            if (_allLobbies == null) return list;

            // Filter, then sort, then wrap as view rows.
            IEnumerable<LobbyData> filtered = _allLobbies.Where(_filters.Passes);
            IEnumerable<LobbyData> sorted   = LobbySorter.Sort(filtered, _sortAttribute);

            foreach (var lobby in sorted)
            {
                list.Add(new LobbyViewType(this, lobby));
            }
            return list;
        }

        // ---------- Action handlers (stubs while the netcode isn't wired) ----------

        public void JoinLobby(LobbyData lobby)
        {
            YargLogger.LogInfo($"OnlineMenu: join lobby — host={lobby.HostName}, song={lobby.SongName}");
            // TODO: hand off to netcode + transition to lobby room scene
        }

        private void CreateLobby()
        {
            YargLogger.LogInfo("OnlineMenu: create-lobby pressed");
            // TODO: open create-lobby dialog (song / type / privacy / capacity)
        }

        private void OpenFilters()
        {
            // For now just cycle through a couple of states so we can see the
            // sort/filter pipeline working from the browser. Replace with a
            // proper filters popup once the prefab is built.
            _filters.ShowFullLobbies = !_filters.ShowFullLobbies;
            _sortAttribute = _sortAttribute == LobbySortAttribute.SongName
                ? LobbySortAttribute.HostName
                : LobbySortAttribute.SongName;

            YargLogger.LogInfo(
                $"OnlineMenu: filters cycled — sort={_sortAttribute}, " +
                $"showFull={_filters.ShowFullLobbies}, showPrivate={_filters.ShowPrivateLobbies}, " +
                $"type={_filters.OnlyType}, state={_filters.OnlyState}");

            RequestViewListUpdate();
        }

        private void JoinByCode()
        {
            YargLogger.LogInfo("OnlineMenu: join-by-code pressed");
            // TODO: open code-entry dialog → look up lobby via netcode → JoinLobby
        }

        public void Back()
        {
            MenuManager.Instance.PopMenu();
        }
    }
}
