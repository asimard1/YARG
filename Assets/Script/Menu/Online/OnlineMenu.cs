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
    /// Lobby browser. Re-seeds the list on every <c>LobbiesChanged</c> event from <see cref="LobbyHubSession"/>.
    /// </summary>
    public class OnlineMenu : ListMenu<LobbyViewType, LobbyView>
    {
        protected override int ExtraListViewPadding => 12;

        // Gates the one-shot scroll-to-top on initial population.
        private bool _initialLobbyLoadDone;

        [SerializeField]
        private TextMeshProUGUI _lobbyCountText;

        [SerializeField]
        private TMP_InputField _searchField;
        private string _searchQuery = string.Empty;

        [SerializeField]
        private CreateLobbyMenu _createLobbyPopup;
        [SerializeField]
        private JoinByCodeMenu _joinByCodePopup;
        [SerializeField]
        private LobbyFiltersMenu _filtersMenu;

        // Dedicated sort button + label. Clicking cycles through sort attributes.
        [SerializeField]
        private Button _sortButton;
        [SerializeField]
        private TextMeshProUGUI _sortButtonLabel;

        // Master lobby list, re-derived on LobbiesChanged.
        private List<LobbyData> _allLobbies = new();

        // Active sort/filter state for this browser session.
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

        // Cached for safe unsubscribe even if Current changes.
        private LobbyHubSession _boundSession;

        protected override void Awake()
        {
            base.Awake();
            // Sort button cycles through sort attributes.
            if (_sortButton != null)
            {
                _sortButton.onClick.RemoveAllListeners();
                _sortButton.onClick.AddListener(CycleSort);
            }

            // Re-derive the list on any text change.
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
        /// Advance the active sort attribute one step.
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
            _sortButtonLabel.text = $"{Localize.Key("Menu.Online.FiltersMenu.SortedBy")}: {value}";
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            PushNavigationScheme();

            // Reset so the first lobby load snaps SelectedIndex to 0.
            _initialLobbyLoadDone = false;

            _boundSession = LobbyHubSession.Current;
            if (_boundSession == null)
            {
                // LAN-only mode -- no session, but still render so the user can host/join LAN.
                YargLogger.LogWarning("OnlineMenu: opened without an active session -- rendering in LAN-only mode (empty lobby list)");
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
            YargLogger.LogInfo($"OnlineMenu: lobby list updated -- {_allLobbies.Count} lobbies");
            RefreshLobbyList();
            ScrollToTopOnInitialLoad();
        }

        // One-shot: first load forces SelectedIndex to 0. Deferred one frame
        // so Unity's layout pass sizes the row RectTransforms before we read heights.
        private void ScrollToTopOnInitialLoad()
        {
            if (_initialLobbyLoadDone) return;
            _initialLobbyLoadDone = true;
            ScrollToTopNextFrameAsync().Forget();
        }

        private async UniTaskVoid ScrollToTopNextFrameAsync()
        {
            // Wait one frame for layout pass.
            await UniTask.NextFrame(PlayerLoopTiming.LastPostLateUpdate);
            if (this == null) return;
            SelectedIndex = 0;
        }

        private async UniTaskVoid EnsureConnectedAsync()
        {
            using var context = new LoadingContext();
            MenuManager.Instance.DisableCurrentMenu();
            context.SetLoadingText("Loading lobbies...");
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

                // Bottom-row action buttons.
                new NavigationScheme.Entry(MenuAction.Yellow, "Menu.Online.CreateLobby",  CreateLobby),
                new NavigationScheme.Entry(MenuAction.Blue,   "Menu.Online.Filters",      OpenFilters),
                new NavigationScheme.Entry(MenuAction.Orange, "Menu.Online.JoinByCode",   JoinByCode),
            }, true));
        }

        // Top-align the list instead of centering the selected row.
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

            // Filter → search → sort → wrap as view rows.
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
            YargLogger.LogInfo($"OnlineMenu: join lobby -- host={lobby.HostName}, song={lobby.SongName}");

            using var context = new LoadingContext();
            MenuManager.Instance.DisableCurrentMenu();
            context.SetLoadingText("Joining lobby...");
            try
            {
                // Send local instrument for other members' player lists.
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
            // Fallback for prefabs that haven't been re-wired to host the inline popup.
            YargLogger.LogWarning("OnlineMenu: _createLobbyPopup is unwired -- falling back to legacy menu transition");
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.CreateLobby);
        }

        private void OpenFilters()
        {
            _filtersMenu.Bind(this);
            _filtersMenu.gameObject.SetActive(true);
        }

        /// <summary>
        /// Re-derive the rendered list after a filter/sort change from the popup.
        /// </summary>
        public void RequestRefreshAfterFilterChange()
        {
            RefreshLobbyList();
        }

        private void JoinByCode()
        {
            YargLogger.LogInfo("OnlineMenu: join-by-code pressed");
            if (_joinByCodePopup != null)
            {
                _joinByCodePopup.gameObject.SetActive(true);
                return;
            }
            YargLogger.LogWarning("OnlineMenu: _joinByCodePopup is unwired");
        }

        public void Back()
        {
            YargLogger.LogInfo("OnlineMenu.Back: triggering session shutdown");
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.MainMenu);
            LobbyHubSession.ShutdownAsync().Forget();
        }
    }
}
