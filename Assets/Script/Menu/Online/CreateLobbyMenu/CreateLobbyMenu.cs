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
// YARG.Core.GameMode (the per-instrument game-mode enum used by profiles)
// collides with the lobby contract's GameMode. Alias to the contract type;
// keep `using YARG.Core` for `Instrument` etc.
using GameMode = YARG.Online.Lobbies.Contracts.Enums.GameMode;
using Region   = YARG.Online.Lobbies.Contracts.Enums.Region;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Popup-style create-lobby form. Each option group is rendered as a
    /// vertical stack of radio-style toggle rows (one row per possible
    /// value) — clicking a row checks it and unchecks its siblings, the
    /// same UX shape as DifficultySelect's instrument / difficulty pickers.
    /// The green "Create" row at the top of the list submits with the
    /// currently-checked values; Red closes the popup.
    ///
    /// Localization keys live under <c>Menu.Online.CreateLobbyMenu.*</c>
    /// (NOT <c>Menu.Online.CreateLobby</c> — that one's the lobby-browser
    /// nav-scheme label for the Yellow "Create Lobby" button).
    ///
    /// Visibility is exposed in the UI but doesn't yet hit the wire —
    /// the contract's <see cref="CreateLobbyArgs"/> has no visibility
    /// field. Selection is stored locally so when the contract grows the
    /// field this menu can ship it straight through.
    /// </summary>
    public class CreateLobbyMenu : MonoBehaviour
    {
        // Server contract clamps the upper bound at 8; we expose 2-4 here
        // since 5+ is rarely a useful YARG lobby size.
        private const int MinPlayers       = 2;
        private const int MaxPlayersCap    = 4;
        private const int DefaultMaxPlayers = 4;

        // Region's been pulled from the UI per design — the contract still
        // requires a value in CreateLobbyArgs, so we ship this default. If
        // the matchmaker grows region-aware routing later, surface it back
        // as a row group alongside the others.
        private const Region DefaultRegion = Region.UsEast;

        // Local-only until the contract grows a field. See class docstring.
        private enum VisibilityChoice
        {
            Public,
            Private,
        }

        // Single text element at the top of the popup. Renders as
        //     "<sprite name=\"{gameMode}\"> {profileName}"
        // matching DifficultySelectMenu's _text field — confirms the
        // creator's identity + active instrument before they hit Create.
        [SerializeField]
        private TextMeshProUGUI _headerText;

        [Space]
        [SerializeField]
        private Transform _container;
        // NavigationGroup is used ONLY for selection-state coordination
        // (so clicking a row deactivates the previously-selected row's
        // _selectedVisual). No nav scheme is pushed — the popup is
        // pointer-driven content. Without this, every row that gets
        // clicked has NavigatableBehaviour.OnPointerDown call
        // SetSelected(true) and the group-managed "only one selected
        // at a time" invariant never fires, so the selection visual
        // sticks on every clicked row.
        [SerializeField]
        private NavigationGroup _navGroup;
        // Three prefab slots:
        //   - _itemPrefab: header + body cycling row used for every
        //     option (Game Mode, Max Players, Visibility). Click advances
        //     the value; SetBody updates the body text in place.
        //   - _itemGreenPrefab: green-styled action row for "Create" at
        //     the top of the list.
        //   - _itemInputPrefab: inline TMP_InputField row for the lobby
        //     name.
        [SerializeField]
        private CreateLobbyMenuItem _itemPrefab;
        [SerializeField]
        private CreateLobbyMenuItem _itemGreenPrefab;
        [SerializeField]
        private CreateLobbyMenuItemInput _itemInputPrefab;

        private GameMode         _gameMode    = GameMode.Band;
        private int              _maxPlayers  = DefaultMaxPlayers;
        private VisibilityChoice _visibility  = VisibilityChoice.Public;

        // Live references for in-place body updates. Every option is a
        // cycling row (header label + body showing the current value);
        // SetBody on click writes the new body text directly so the
        // row's TMP refreshes without re-instantiating.
        private CreateLobbyMenuItem      _gameModeRow;
        private CreateLobbyMenuItem      _maxPlayersRow;
        private CreateLobbyMenuItem      _visibilityRow;
        private CreateLobbyMenuItemInput _nameRow;

        private void OnEnable()
        {
            // Push a nav scheme so keyboard / gamepad can drive the popup:
            // Up/Down walks the rows, Green confirms the selected row
            // (advances a cycle / focuses the name input / submits Create),
            // Red closes the popup. NavigateSelect routes Green through
            // CurrentNavigationGroup.ConfirmSelection(), so we also push
            // _navGroup onto the nav-group stack below to make it the
            // active target.
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

        // Mirror DifficultySelectMenu's _text rendering:
        //   <sprite name="{profile.GameMode}"> {profile.Name}
        // Falls back to the session's sanitized display name when no local
        // profile exists, then to a generic "Host" so the header never
        // renders blank.
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

            // Top of the list: the submit action, rendered with the green-
            // styled prefab variant. Pre-selected when the popup opens so
            // the host can mash Green to ship with the current config.
            CreateActionRow(
                _itemGreenPrefab,
                Localize.Key("Menu.Online.CreateLobbyMenu.Create"),
                Confirm);

            // Inline lobby-name row sits just below Create. Pre-populated
            // with "{ProfileName}'s Room" so the host doesn't have to type
            // anything for the common case; they can still focus the row
            // (Green) and edit if they want a custom name.
            _nameRow = CreateNameRow();

            // Game Mode — cycling row (Band ↔ Quickplay). Body shows the
            // current value; clicking advances and re-renders the body.
            _gameModeRow = CreateCycleRow(
                Localize.Key("Menu.Online.CreateLobbyMenu.GameMode"),
                FormatGameMode(_gameMode),
                CycleGameMode);

            // Max Players — cycling row over 2..MaxPlayersCap (2 → 3 → 4 → 2).
            _maxPlayersRow = CreateCycleRow(
                Localize.Key("Menu.Online.CreateLobbyMenu.MaxPlayers"),
                FormatMaxPlayers(_maxPlayers),
                CycleMaxPlayers);

            // Visibility — cycling row (Public ↔ Private With Code). Same
            // shape as the other option rows for consistency; the user
            // moved away from the ModifierItem-toggle UX to keep every
            // option using one prefab type.
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

        // Instantiate the inline name-input row + seed it with the default
        // lobby name. Registered with the nav group so its selection
        // visual clears when the user clicks something else.
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

        // Cycling row: header label + body showing the current value.
        // Clicking the button advances the value and the handler updates
        // the body text in place via SetBody.
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

        // Lobby name default — prefilled into the input row on each enable.
        // Falls back through profile.Name → session display name → a
        // static placeholder so the field never starts empty.
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

            // Read the (possibly-edited) name from the inline input row.
            // Trim then fall back to the auto-derived default if the user
            // emptied the input — better than rejecting the submit with
            // an error.
            string name = _nameRow != null ? _nameRow.Text?.Trim() : null;
            if (string.IsNullOrEmpty(name))
            {
                name = DefaultLobbyName();
            }

            using var context = new LoadingContext();
            context.SetLoadingText("Creating lobby…");
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
                };

                // TODO: thread _visibility through CreateLobbyArgs once the
                // server contract grows a visibility / private-code field.

                await session.CreateLobbyAsync(args, CancellationToken.None);

                // Pop the popup; the lobby session will report the new
                // lobby and the LobbyView take over.
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
