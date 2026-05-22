using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Player;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Prototype create-lobby form. Collects a name from the user; every other
    /// <see cref="CreateLobbyArgs"/> field (game mode, region, max players,
    /// song, library) is hardcoded for now. On Confirm, invokes
    /// <c>ILobbyHub.CreateLobby</c> via <see cref="LobbyHubSession"/> and
    /// transitions to the LobbyView once <see cref="LobbyHubSession.CurrentLobby"/>
    /// is populated.
    /// </summary>
    public class CreateLobbyMenu : MonoBehaviour
    {
        // Defaults until the form grows dropdowns for these.
        private const GameMode DefaultGameMode   = GameMode.Band;
        private const Region   DefaultRegion     = Region.UsEast;
        private const int      DefaultMaxPlayers = 4;

        [SerializeField]
        private TMP_InputField _nameField;
        // _passwordField has no contract equivalent yet — it's dead UI. Left
        // serialized so the prefab doesn't lose the binding when we wire it up.
        [SerializeField]
        private TMP_InputField _passwordField;

        private void OnEnable()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", Confirm),
                new NavigationScheme.Entry(MenuAction.Red,   "Menu.Common.Back",    Back),
            }, true));

            if (_nameField     != null) _nameField.text     = string.Empty;
            if (_passwordField != null) _passwordField.text = string.Empty;
        }

        private void OnDisable()
        {
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        public void Confirm() => ConfirmAsync().Forget();

        private async UniTaskVoid ConfirmAsync()
        {
            string name = _nameField != null ? _nameField.text?.Trim() : null;
            if (string.IsNullOrEmpty(name))
            {
                YargLogger.LogInfo("CreateLobbyMenu: confirm blocked — name is empty");
                return;
            }

            var session = LobbyHubSession.Current;
            if (session == null)
            {
                DialogManager.Instance.ShowMessage("Could not create lobby",
                    "The lobby session is no longer active.");
                return;
            }

            using var context = new LoadingContext();
            context.SetLoadingText("Creating lobby…");
            try
            {
                // Sent so the server can broadcast our instrument to other members for
                // display in their player lists. Defaults to 0 if no local profile is active.
                byte localInstrument = PlayerContainer.Players.Count > 0
                    ? (byte) PlayerContainer.Players[0].Profile.CurrentInstrument
                    : (byte) 0;

                var args = new CreateLobbyArgs(
                    Name:       name,
                    GameMode:   DefaultGameMode,
                    Region:     DefaultRegion,
                    Song:       null,
                    MaxPlayers: DefaultMaxPlayers,
                    Library:    LocalSongLibrary.BuildLocal())
                {
                    Instrument = localInstrument,
                };

                await session.CreateLobbyAsync(args, CancellationToken.None);

                // Swap ourselves out for the lobby view. Back from LobbyView
                // returns explicitly to the OnlineMenu (browser) via
                // SetActiveMenuExclusive, so this form doesn't need to linger.
                MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.LobbyView);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                DialogManager.Instance.ShowMessage("Could not create lobby", ex.Message);
            }
        }

        public void Back()
        {
            MenuManager.Instance.SetActiveMenuExclusive(MenuManager.Menu.Online);
        }
    }
}
