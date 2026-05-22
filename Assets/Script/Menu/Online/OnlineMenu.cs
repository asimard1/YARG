using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Localization;
using YARG.Core.Logging;
using YARG.Menu.ListMenu;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Player;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Lobby browser. Modeled after <c>MusicLibraryMenu</c> but stripped down
    /// to just rows-of-lobbies with sort/filter — no playlists, recommendations,
    /// shows, previews, scoring, stars or difficulty. Source of truth for the
    /// list is <see cref="LobbyHubSession"/>; we re-seed on every
    /// <c>LobbiesChanged</c> event.
    /// </summary>
    public class OnlineMenu : ListMenu<LobbyViewType, LobbyView>
    {
        protected override int ExtraListViewPadding => 12;

        // Tracks whether OnLobbiesChanged has run at least once during this
        // OnEnable cycle, so the initial population can force SelectedIndex
        // back to 0.
        private bool _initialLobbyLoadDone;

        [SerializeField]
        private TextMeshProUGUI _lobbyCountText;

        [SerializeField]
        private TMP_InputField _searchField;
        private string _searchQuery = string.Empty;

        [SerializeField]
        private CreateLobbyMenu _createLobbyPopup;
        [SerializeField]
        private LobbyFiltersMenu _filtersMenu;

        // Dedicated sort button + its current-state label, modeled on
        // MusicLibrary's NextSort flow. Lives at the top of the lobby
        // browser as a standalone affordance (not bundled into the filters
        // popup) since sort is one-click-cycles and benefits from being
        // permanently visible.
        [SerializeField]
        private Button _sortButton;
        [SerializeField]
        private TextMeshProUGUI _sortButtonLabel;

        // Master list of lobbies derived from the hub cache. Re-derived whenever
        // LobbyHubSession.LobbiesChanged fires (always on the main thread).
        private List<LobbyData> _allLobbies = new();

        // Active sort/filter state. Survives only for the lifetime of the
        // browser; persist via SettingsManager later if needed.
        private LobbySortAttribute  _sortAttribute = LobbySortAttribute.LobbyName;
        private LobbyFilterSettings _filters       = new();

        public LobbySortAttribute  SortAttribute
        {
            get => _sortAttribute;
            set
            {
                _sortAttribute = value;
                RefreshLobbyList();
                UpdateSortButtonLabel();
            }
        }
        public LobbyFilterSettings Filters       => _filters;

        // Cached session reference so we can unsubscribe at OnDisable even if
        // LobbyHubSession.Current has been replaced/nulled by then.
        private LobbyHubSession _boundSession;

        protected override void Awake()
        {
            base.Awake();
            // Hook the dedicated sort button (if wired) — clicking cycles
            // through the sort attributes and re-renders the list. The
            // SortAttribute property's setter handles the refresh, so the
            // click just advances the enum and lets the property do the
            // work.
            if (_sortButton != null)
            {
                _sortButton.onClick.RemoveAllListeners();
                _sortButton.onClick.AddListener(CycleSort);
            }

            // Search field is wired in the prefab; subscribe here so any text
            // change re-derives the rendered list. No debounce needed — the
            // lobby list is short and CreateViewList is O(n) on a few hundred
            // entries at most.
            if (_searchField != null)
            {
                _searchField.onValueChanged.RemoveListener(OnSearchTextChanged);
                _searchField.onValueChanged.AddListener(OnSearchTextChanged);
            }

            UpdateSortButtonLabel();
            RefreshLobbyList();
        }

        private void OnSearchTextChanged(string text)
        {
            _searchQuery = text ?? string.Empty;
            RefreshLobbyList();
        }

        /// <summary>
        /// Advance the active sort attribute one step. Wired to the on-screen
        /// Sort button; also callable from code if a future surface wants
        /// to drive sort without going through the button. SortAttribute's
        /// setter triggers RefreshLobbyList + UpdateSortButtonLabel, so the
        /// list and the label update together.
        /// </summary>
        public void CycleSort()
        {
            // Walk LobbyName → HostName → SongName → PlayerCount → wrap.
            SortAttribute = SortAttribute switch
            {
                LobbySortAttribute.LobbyName   => LobbySortAttribute.HostName,
                LobbySortAttribute.HostName    => LobbySortAttribute.SongName,
                LobbySortAttribute.SongName    => LobbySortAttribute.PlayerCount,
                LobbySortAttribute.PlayerCount => LobbySortAttribute.LobbyName,
                _                              => LobbySortAttribute.LobbyName,
            };
        }

        private void UpdateSortButtonLabel()
        {
            if (_sortButtonLabel == null) return;
            string value = _sortAttribute switch
            {
                LobbySortAttribute.LobbyName   => Localize.Key("Menu.Online.FiltersMenu.SortBy.LobbyName"),
                LobbySortAttribute.HostName    => Localize.Key("Menu.Online.FiltersMenu.SortBy.HostName"),
                LobbySortAttribute.SongName    => Localize.Key("Menu.Online.FiltersMenu.SortBy.SongName"),
                LobbySortAttribute.PlayerCount => Localize.Key("Menu.Online.FiltersMenu.SortBy.PlayerCount"),
                _                              => _sortAttribute.ToString(),
            };
            _sortButtonLabel.text = $"{Localize.Key("Menu.Online.FiltersMenu.SortBy")}: {value}";
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            PushNavigationScheme();

            // Reset the "have we received an initial lobby list yet?" gate so
            // the first OnLobbiesChanged of this OnEnable cycle (or the LAN-
            // only RefreshLobbyList below) snaps SelectedIndex back to the
            // top. Without this, returning to the browser keeps the cursor
            // at whatever row was last selected — visually jarring after a
            // full list refresh.
            _initialLobbyLoadDone = false;

            _boundSession = LobbyHubSession.Current;
            if (_boundSession == null)
            {
                // LAN-only mode — MainMenu.Online couldn't reach the matchmaking
                // server (auth or SignalR connect failed) and surfaced a warning
                // dialog explaining as much. We still want the menu to render so
                // the user can host/join a LAN game; the public lobby list will
                // just be empty since no session is feeding it. Earlier this
                // path forced a kick back to MainMenu, which made it look like
                // the Online button itself was broken.
                YargLogger.LogWarning("OnlineMenu: opened without an active session — rendering in LAN-only mode (empty lobby list)");
                RefreshLobbyList();
                ScrollToTopOnInitialLoad();
                return;
            }

            _boundSession.LobbiesChanged += OnLobbiesChanged;

            if (_boundSession.State == LobbyHubSession.ConnectionState.Connected)
            {
                OnLobbiesChanged();
            }
            else
            {
                EnsureConnectedAsync().Forget();
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_boundSession != null)
            {
                _boundSession.LobbiesChanged -= OnLobbiesChanged;
                _boundSession = null;
            }
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        private void OnLobbiesChanged()
        {
            if (_boundSession == null) return;
            _allLobbies = _boundSession.Lobbies.Select(LobbyMapper.FromDto).ToList();
            YargLogger.LogInfo($"OnlineMenu: lobby list updated — {_allLobbies.Count} lobbies");
            RefreshLobbyList();
            ScrollToTopOnInitialLoad();
        }

        // One-shot: the first OnLobbiesChanged of an OnEnable cycle forces
        // SelectedIndex back to the top of the freshly-rebuilt list.
        // Subsequent updates (a new lobby joining the cache, a song change,
        // etc.) preserve whatever the user is currently looking at.
        //
        // Deferred by one frame so Unity's layout pass has a chance to size
        // the just-instantiated row RectTransforms before
        // RealignParentViewObject reads their .rect.height values — without
        // the defer, the heights all measure zero on the first synchronous
        // call, the OnlineMenu's GetViewParentOffsetY override falls back
        // to a centered offset, and the list visibly snaps to the top only
        // once the user starts scrolling (which triggers another re-align
        // pass against now-valid heights).
        private void ScrollToTopOnInitialLoad()
        {
            if (_initialLobbyLoadDone) return;
            _initialLobbyLoadDone = true;
            ScrollToTopNextFrameAsync().Forget();
        }

        private async UniTaskVoid ScrollToTopNextFrameAsync()
        {
            // One frame is enough for Unity to run a layout pass + give the
            // row RectTransforms valid sizes. We use the unscaled clock so
            // the defer still resolves if SongSpeed is < 1x somewhere up
            // the stack (defensive — the lobby browser doesn't see song
            // speed today, but the cancellation token threading needs a
            // clock that always advances).
            await UniTask.NextFrame(PlayerLoopTiming.LastPostLateUpdate);
            if (this == null) return;
            SelectedIndex = 0;
        }

        private async UniTaskVoid EnsureConnectedAsync()
        {
            using var context = new LoadingContext();
            context.SetLoadingText("Loading lobbies…");
            try
            {
                if (_boundSession == null) return;
                await _boundSession.ConnectAsync();
                OnLobbiesChanged();
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not load lobbies",
                    "Failed to connect to the YARG server. Check that it's running and try again.");
            }
        }

        private void RefreshLobbyList()
        {
            RequestViewListUpdate();
            UpdateLobbyCountText();
        }

        private void UpdateLobbyCountText()
        {
            if (_lobbyCountText == null) return;

            int count = ViewList?.Count ?? 0;
            _lobbyCountText.text = count == 1 ? "1 Lobby" : $"{count} Lobbies";
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
            }, true));
        }

        // Top-align the list instead of centering the selected row. The lobby
        // browser usually has few entries, so anchoring the selection to the
        // top reads better than MusicLibrary's centered selection. The offset
        // is mask-relative so the selected row sits flush with the mask top
        // regardless of how tall the visible window actually is.
        protected override float GetViewParentOffsetY(float topHeight, float bottomHeight)
        {
            var maskRect = (RectTransform) _viewObjectParent.parent;
            float rowHeight = ExtraListViewPadding > 0 ? topHeight / ExtraListViewPadding : 0;
            return (maskRect.rect.height - rowHeight) / 2;
        }

        protected override List<LobbyViewType> CreateViewList()
        {
            var list = new List<LobbyViewType>();
            if (_allLobbies == null) return list;

            // Filter, then search-match, then sort, then wrap as view rows.
            // Search runs on the post-filter pool so an active filter never
            // surfaces hidden lobbies just because their name matched.
            IEnumerable<LobbyData> filtered = _allLobbies.Where(_filters.Passes);
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                string needle = _searchQuery.Trim();
                filtered = filtered.Where(l =>
                    !string.IsNullOrEmpty(l.LobbyName) &&
                    l.LobbyName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            IEnumerable<LobbyData> sorted = LobbySorter.Sort(filtered, _sortAttribute);

            foreach (var lobby in sorted)
            {
                list.Add(new LobbyViewType(this, lobby));
            }
            return list;
        }

        // ---------- Action handlers ----------

        public void JoinLobby(LobbyData lobby) => JoinLobbyAsync(lobby).Forget();

        private async UniTaskVoid JoinLobbyAsync(LobbyData lobby)
        {
            YargLogger.LogInfo($"OnlineMenu: join lobby — host={lobby.HostName}, song={lobby.SongName}");

            using var context = new LoadingContext();
            context.SetLoadingText("Joining lobby…");
            try
            {
                // Sent so the server can broadcast our instrument to other members for
                // display in their player lists. Defaults to 0 if no local profile is active.
                byte localInstrument = PlayerContainer.Players.Count > 0
                    ? (byte) PlayerContainer.Players[0].Profile.CurrentInstrument
                    : (byte) 0;
                var args = new EnterLobbyArgs(lobby.LobbyId, LocalSongLibrary.BuildLocal())
                {
                    Instrument = localInstrument,
                };
                var session = LobbyHubSession.Current;
                if (session == null)
                {
                    DialogManager.Instance.ShowMessage("Could not join lobby",
                        "The lobby session is no longer active.");
                    return;
                }
                await session.EnterLobbyAsync(args, CancellationToken.None);
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.LobbyView);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not join lobby", ex.Message);
                // Online is still active; nothing to re-show.
            }
        }

        private void CreateLobby()
        {
            YargLogger.LogInfo("OnlineMenu: create-lobby pressed");
            if (_createLobbyPopup != null)
            {
                _createLobbyPopup.gameObject.SetActive(true);
                return;
            }
            // Fallback for prefabs that haven't been re-wired to host the
            // inline popup yet. The Menu.CreateLobby entry remains in
            // MenuManager so legacy navigation keeps working.
            YargLogger.LogWarning("OnlineMenu: _createLobbyPopup is unwired — falling back to legacy menu transition");
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.CreateLobby);
        }

        private void OpenFilters()
        {
            _filtersMenu.Bind(this);
            _filtersMenu.gameObject.SetActive(true);
        }

        /// <summary>
        /// External hook for the filters popup: re-derive the rendered list
        /// after a filter / sort change. The popup writes directly to the
        /// owner's <see cref="Filters"/> object (so any update is already
        /// visible to <see cref="CreateViewList"/>), then calls this to
        /// trigger the refresh.
        /// </summary>
        public void RequestRefreshAfterFilterChange()
        {
            RefreshLobbyList();
        }

        private void JoinByCode()
        {
            YargLogger.LogInfo("OnlineMenu: join-by-code pressed");
            // TODO: open code-entry dialog → ILobbyHub.EnterLobby
        }

        public void Back()
        {
            YargLogger.LogInfo("OnlineMenu.Back: triggering session shutdown");
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.MainMenu);
            LobbyHubSession.ShutdownAsync().Forget();
        }
    }
}
