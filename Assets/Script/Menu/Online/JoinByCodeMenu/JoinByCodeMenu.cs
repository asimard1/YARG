using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Helpers.Extensions;
using YARG.Core.Input;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Player;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Popup-style join-by-code form with a green "Join" action and an inline code input.
    /// </summary>
    public class JoinByCodeMenu : MonoBehaviour
    {
        // Shows instrument sprite + profile name.
        [SerializeField]
        private TextMeshProUGUI _headerText;

        [Space]
        [SerializeField]
        private Transform _container;
        // Selection-state coordination only -- ensures only one row is highlighted at a time.
        [SerializeField]
        private NavigationGroup _navGroup;
        // Green action row and inline input row prefabs (shared with CreateLobbyMenu).
        [SerializeField]
        private CreateLobbyMenuItem _itemGreenPrefab;
        [SerializeField]
        private CreateLobbyMenuItemInput _itemInputPrefab;

        private CreateLobbyMenuItemInput _codeRow;

        private void OnEnable()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back),
            }, true));

            RefreshHeader();
            BuildRows();

            if (_navGroup != null)
            {
                _navGroup.PushNavGroupToStack();
                _navGroup.SelectFirst();
            }
        }

        private void OnDisable()
        {
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        private void RefreshHeader()
        {
            if (_headerText == null) return;

            if (PlayerContainer.Players.Count > 0 && PlayerContainer.Players[0].Profile is { } profile)
            {
                _headerText.text = $"<sprite name=\"{profile.GameMode.ToResourceName()}\"> {profile.Name}";
                return;
            }

            _headerText.text = LobbyHubSession.Current?.LocalDisplayName ?? "Guest";
        }

        private void BuildRows()
        {
            if (_navGroup != null) _navGroup.ClearNavigatables();
            _container.DestroyChildren();
            _codeRow = null;

            // Green "Join" action at the top.
            var joinRow = Instantiate(_itemGreenPrefab, _container);
            joinRow.InitializeAction(Localize.Key("Menu.Online.JoinByCodeMenu.Join"), Confirm);
            if (_navGroup != null && joinRow.Button != null) _navGroup.AddNavigatable(joinRow.Button);

            // Inline code input.
            _codeRow = Instantiate(_itemInputPrefab, _container);
            _codeRow.Initialize(
                Localize.Key("Menu.Online.JoinByCodeMenu.Code"),
                string.Empty,
                Localize.Key("Menu.Online.JoinByCodeMenu.CodePlaceholder"));
            if (_navGroup != null) _navGroup.AddNavigatable(_codeRow);
        }

        public void Confirm() => ConfirmAsync().Forget();

        private async UniTaskVoid ConfirmAsync()
        {
            var session = LobbyHubSession.Current;
            if (session == null)
            {
                DialogManager.Instance.ShowMessage("Could not join lobby",
                    "The lobby session is no longer active.");
                return;
            }

            // Uppercase so the server-side LobbyId regex matches regardless of casing.
            string code = _codeRow != null ? _codeRow.Text?.Trim().ToUpperInvariant() : null;
            if (string.IsNullOrEmpty(code))
            {
                DialogManager.Instance.ShowMessage("Could not join lobby",
                    "Enter a lobby code.");
                return;
            }

            using var context = new LoadingContext();
            MenuManager.Instance.DisableCurrentMenu();
            context.SetLoadingText("Joining lobby...");
            try
            {
                byte localInstrument = PlayerContainer.Players.Count > 0
                    ? (byte) PlayerContainer.Players[0].Profile.CurrentInstrument
                    : (byte) 0;

                var args = new EnterLobbyArgs(code, LocalSongLibrary.BuildLocal())
                {
                    Instrument = localInstrument,
                };

                await session.EnterLobbyAsync(args, CancellationToken.None);

                gameObject.SetActive(false);
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.LobbyView);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not join lobby", ex.Message);
            }
        }

        public void Back()
        {
            gameObject.SetActive(false);
        }
    }
}
