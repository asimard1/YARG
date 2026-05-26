using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Helpers.Extensions;
using YARG.Core.Input;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Online;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Player;
// Alias to avoid collision with YARG.Core.GameMode.
using GameMode = YARG.Online.Lobbies.Contracts.Enums.GameMode;
using Region   = YARG.Online.Lobbies.Contracts.Enums.Region;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Popup-style create-lobby form with cycling option rows and a green "Create" action.
    /// Localization keys live under <c>Menu.Online.CreateLobbyMenu.*</c>.
    /// </summary>
    public class CreateLobbyMenu : MonoBehaviour
    {
        // Expose 2-4; server caps at 8.
        private const int MinPlayers       = 2;
        private const int MaxPlayersCap    = 4;
        private const int DefaultMaxPlayers = 4;

        // Region pulled from UI but still required by CreateLobbyArgs.
        private const Region DefaultRegion = Region.UsEast;

        // Local-only until the contract adds a visibility field.
        private enum VisibilityChoice
        {
            Public,
            Private,
        }

        // Renders "<sprite name='{gameMode}'> {profileName}" at the top of the popup.
        [SerializeField]
        private TextMeshProUGUI _headerText;

        [Space]
        [SerializeField]
        private Transform _container;
        // Selection-state coordination only -- ensures only one row is highlighted at a time.
        [SerializeField]
        private NavigationGroup _navGroup;
        // Cycling row, green action row, and inline input row prefabs.
        [SerializeField]
        private CreateLobbyMenuItem _itemPrefab;
        [SerializeField]
        private CreateLobbyMenuItem _itemGreenPrefab;
        [SerializeField]
        private CreateLobbyMenuItemInput _itemInputPrefab;

        private GameMode         _gameMode    = GameMode.Band;
        private int              _maxPlayers  = DefaultMaxPlayers;
        private VisibilityChoice _visibility  = VisibilityChoice.Public;

        // Live references for in-place body text updates on click.
        private CreateLobbyMenuItem      _gameModeRow;
        private CreateLobbyMenuItem      _maxPlayersRow;
        private CreateLobbyMenuItem      _visibilityRow;
        private CreateLobbyMenuItemInput _nameRow;

        private void OnEnable()
        {
            // Keyboard / gamepad: Up/Down walks rows, Green confirms, Red closes.
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back),
            }, true));

            RefreshHeader();
            BuildOptions();

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

        // Shows instrument sprite + profile name; falls back to display name or "Host".
        private void RefreshHeader()
        {
            if (_headerText == null) return;

            if (PlayerContainer.Players.Count > 0 && PlayerContainer.Players[0].Profile is { } profile)
            {
                _headerText.text = $"<sprite name=\"{profile.GameMode.ToResourceName()}\"> {profile.Name}";
                return;
            }

            _headerText.text = LobbyHubSession.Current?.LocalDisplayName ?? "Host";
        }

        private void BuildOptions()
        {
            if (_navGroup != null) _navGroup.ClearNavigatables();
            _container.DestroyChildren();
            _gameModeRow   = null;
            _maxPlayersRow = null;
            _visibilityRow = null;
            _nameRow       = null;

            // Green "Create" action at the top.
            CreateActionRow(
                _itemGreenPrefab,
                Localize.Key("Menu.Online.CreateLobbyMenu.Create"),
                Confirm);

            // Inline lobby-name row, pre-populated with "{ProfileName}'s Room".
            _nameRow = CreateNameRow();

            // Game Mode cycling row.
            _gameModeRow = CreateCycleRow(
                Localize.Key("Menu.Online.CreateLobbyMenu.GameMode"),
                FormatGameMode(_gameMode),
                CycleGameMode);

            // Max Players cycling row.
            _maxPlayersRow = CreateCycleRow(
                Localize.Key("Menu.Online.CreateLobbyMenu.MaxPlayers"),
                FormatMaxPlayers(_maxPlayers),
                CycleMaxPlayers);

            // Visibility cycling row.
            _visibilityRow = CreateCycleRow(
                Localize.Key("Menu.Online.CreateLobbyMenu.Visibility"),
                FormatVisibility(_visibility),
                CycleVisibility);
        }

        private CreateLobbyMenuItem CreateActionRow(CreateLobbyMenuItem prefab, string label, Action onClick)
        {
            var row = Instantiate(prefab, _container);
            row.InitializeAction(label, () => onClick());
            if (_navGroup != null && row.Button != null) _navGroup.AddNavigatable(row.Button);
            return row;
        }

        // Instantiate the inline name-input row with the default lobby name.
        private CreateLobbyMenuItemInput CreateNameRow()
        {
            var row = Instantiate(_itemInputPrefab, _container);
            row.Initialize(
                Localize.Key("Menu.Online.CreateLobbyMenu.LobbyName"),
                DefaultLobbyName(),
                Localize.Key("Menu.Online.CreateLobbyMenu.LobbyNamePlaceholder"));
            if (_navGroup != null) _navGroup.AddNavigatable(row);
            return row;
        }

        // Cycling row: click advances the value and updates the body text.
        private CreateLobbyMenuItem CreateCycleRow(string header, string body, Action onClick)
        {
            var row = Instantiate(_itemPrefab, _container);
            row.Initialize(header, body, () => onClick());
            if (_navGroup != null && row.Button != null) _navGroup.AddNavigatable(row.Button);
            return row;
        }

        // ---- Cycle handlers ------------------------------------------------

        private void CycleGameMode()
        {
            _gameMode = NextEnum(_gameMode);
            _gameModeRow?.SetBody(FormatGameMode(_gameMode));
        }

        private void CycleMaxPlayers()
        {
            _maxPlayers++;
            if (_maxPlayers > MaxPlayersCap) _maxPlayers = MinPlayers;
            _maxPlayersRow?.SetBody(FormatMaxPlayers(_maxPlayers));
        }

        private void CycleVisibility()
        {
            _visibility = NextEnum(_visibility);
            _visibilityRow?.SetBody(FormatVisibility(_visibility));
        }

        private static T NextEnum<T>(T value) where T : struct, Enum
        {
            var values = (T[]) Enum.GetValues(typeof(T));
            int idx = Array.IndexOf(values, value);
            return values[(idx + 1) % values.Length];
        }

        // ---- Formatters ----------------------------------------------------

        private static string FormatGameMode(GameMode mode) => mode switch
        {
            GameMode.Band      => Localize.Key("Menu.Online.CreateLobbyMenu.GameMode.Band"),
            GameMode.Quickplay => Localize.Key("Menu.Online.CreateLobbyMenu.GameMode.Quickplay"),
            _                  => mode.ToString(),
        };

        private static string FormatMaxPlayers(int n) =>
            Localize.Key($"Menu.Online.CreateLobbyMenu.MaxPlayers.{n}");

        private static string FormatVisibility(VisibilityChoice v) => v switch
        {
            VisibilityChoice.Public  => Localize.Key("Menu.Online.CreateLobbyMenu.Visibility.Public"),
            VisibilityChoice.Private => Localize.Key("Menu.Online.CreateLobbyMenu.Visibility.Private"),
            _                        => v.ToString(),
        };

        // Falls back through profile name, session display name, or "YARG Room".
        private string DefaultLobbyName()
        {
            string source;
            if (PlayerContainer.Players.Count > 0
                && PlayerContainer.Players[0].Profile is { } profile
                && !string.IsNullOrWhiteSpace(profile.Name))
            {
                source = profile.Name;
            }
            else if (!string.IsNullOrWhiteSpace(LobbyHubSession.Current?.LocalDisplayName))
            {
                source = LobbyHubSession.Current.LocalDisplayName;
            }
            else
            {
                return "YARG Room";
            }
            return $"{source}'s Room";
        }

        public void Confirm() => ConfirmAsync().Forget();

        private async UniTaskVoid ConfirmAsync()
        {
            var session = LobbyHubSession.Current;
            if (session == null)
            {
                DialogManager.Instance.ShowMessage("Could not create lobby",
                    "The lobby session is no longer active.");
                return;
            }

            // Fall back to the default name if the user cleared the input.
            string name = _nameRow != null ? _nameRow.Text?.Trim() : null;
            if (string.IsNullOrEmpty(name))
            {
                name = DefaultLobbyName();
            }

            using var context = new LoadingContext();
            MenuManager.Instance.DisableCurrentMenu();
            context.SetLoadingText("Creating lobby...");
            try
            {
                byte localInstrument = PlayerContainer.Players.Count > 0
                    ? (byte) PlayerContainer.Players[0].Profile.CurrentInstrument
                    : (byte) 0;

                var args = new CreateLobbyArgs(
                    Name:       name,
                    GameMode:   _gameMode,
                    Region:     DefaultRegion,
                    Song:       null,
                    MaxPlayers: _maxPlayers,
                    Library:    LocalSongLibrary.BuildLocal())
                {
                    Instrument = localInstrument,
                    IsPublic   = _visibility == VisibilityChoice.Public,
                };

                await session.CreateLobbyAsync(args, CancellationToken.None);

                // Close popup and navigate to the new lobby.
                gameObject.SetActive(false);
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
            gameObject.SetActive(false);
        }
    }
}
