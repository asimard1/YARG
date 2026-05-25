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
    /// Popup-style join-by-code form. A trimmed sibling of
    /// <see cref="CreateLobbyMenu"/> — same row prefabs and nav model, but only
    /// two rows: a green "Join" action at the top and an inline text input for
    /// the lobby code. Submit calls <c>ILobbyHub.EnterLobby</c> with the entered
    /// code as the LobbyId; the session takes over from there.
    ///
    /// Localization keys live under <c>Menu.Online.JoinByCodeMenu.*</c>.
    /// </summary>
    public class JoinByCodeMenu : MonoBehaviour
    {
        // Renders "<sprite name=\"{gameMode}\"> {profileName}" — same as
        // CreateLobbyMenu, so the user sees who they're joining as.
        [SerializeField]
        private TextMeshProUGUI _headerText;

        [Space]
        [SerializeField]
        private Transform _container;
        // Selection-state coordination only (no scheme is pushed on it). See
        // CreateLobbyMenu's notes — without the NavigationGroup the row
        // selection visual sticks on every clicked row.
        [SerializeField]
        private NavigationGroup _navGroup;
        // Re-use the same two row prefab types as CreateLobbyMenu:
        //   - _itemGreenPrefab: green-styled action row for "Join".
        //   - _itemInputPrefab: inline TMP_InputField row for the code.
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

            // Green "Join" action at the top — pre-selected so the user can mash
            // Green to submit once they've typed the code.
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

            // Lobby codes are emitted from a fixed uppercase alphabet
            // (ABCEFGHJKLMNPQRSTUVWXYZ01234579 — see LobbyId / LobbyIdGenerator),
            // but users commonly type them in lowercase. Uppercase the trimmed input so
            // the server-side LobbyId.IsValid regex matches regardless of casing.
            string code = _codeRow != null ? _codeRow.Text?.Trim().ToUpperInvariant() : null;
            if (string.IsNullOrEmpty(code))
            {
                DialogManager.Instance.ShowMessage("Could not join lobby",
                    "Enter a lobby code.");
                return;
            }

            using var context = new LoadingContext();
            context.SetLoadingText("Joining lobby…");
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
