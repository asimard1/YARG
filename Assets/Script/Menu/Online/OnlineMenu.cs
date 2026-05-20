using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Menu.ListMenu;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;
using YARG.Online.Lobbies.Contracts.Hubs;

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
        protected override int ExtraListViewPadding => 6;

        [SerializeField]
        private TextMeshProUGUI _lobbyCountText;

        // Master list of lobbies derived from the hub cache. Re-derived whenever
        // LobbyHubSession.LobbiesChanged fires (always on the main thread).
        private List<LobbyData> _allLobbies = new();

        // Active sort/filter state. Survives only for the lifetime of the
        // browser; persist via SettingsManager later if needed.
        private LobbySortAttribute  _sortAttribute = LobbySortAttribute.SongName;
        private LobbyFilterSettings _filters       = new();

        public LobbySortAttribute  SortAttribute { get => _sortAttribute; set { _sortAttribute = value; RefreshLobbyList(); } }
        public LobbyFilterSettings Filters       => _filters;

        // Cached session reference so we can unsubscribe at OnDisable even if
        // LobbyHubSession.Current has been replaced/nulled by then.
        private LobbyHubSession _boundSession;

        protected override void Awake()
        {
            base.Awake();
            RefreshLobbyList();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            PushNavigationScheme();

            _boundSession = LobbyHubSession.Current;
            if (_boundSession == null)
            {
                YargLogger.LogError("OnlineMenu: opened without an active session");
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.MainMenu);
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

            // Filter, then sort, then wrap as view rows.
            IEnumerable<LobbyData> filtered = _allLobbies.Where(_filters.Passes);
            IEnumerable<LobbyData> sorted   = LobbySorter.Sort(filtered, _sortAttribute);

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
                var args = new EnterLobbyArgs(lobby.LobbyId, LocalSongLibrary.BuildLocal());
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
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.CreateLobby);
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
                $"showFull={_filters.ShowFullLobbies}, " +
                $"mode={_filters.OnlyGameMode}, status={_filters.OnlyStatus}");

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
