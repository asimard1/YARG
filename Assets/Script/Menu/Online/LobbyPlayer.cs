using System;
using TMPro;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Menu;
using YARG.Online;

namespace YARG.Menu.Online
{
    public class LobbyPlayer : MonoBehaviour
    {
        // Stage → name tint. Three distinct colours so observers in the lobby can
        // read the active members' status at a glance:
        //   White  = InLobby   (back in the lobby / song-select screen).
        //   Yellow = InGame    (song actively playing).
        //   Orange = OnResults (song finished, viewing the per-player results).
        // The host's Start button is gated on everyone being InLobby.
        private static readonly Color ReadyColor    = Color.white;
        private static readonly Color InGameColor   = new(1f, 0.85f, 0.2f, 1f);
        private static readonly Color OnResultsColor = new(1f, 0.55f, 0.15f, 1f);

        [SerializeField]
        private TextMeshProUGUI _playerNameText;

        // Single host-action entry point. Hidden for non-hosts and for the
        // host's own row (you can't kick yourself / re-host yourself).
        // Clicking opens a shared LobbyPlayerActionsPopup that lists Make
        // Host + Kick targeting this row's member.
        [SerializeField]
        private IconButton _editButton;

        private Action _onKick;
        private Action _onMakeHost;
        private string _displayName;
        private LobbyPlayerActionsPopup _actionsPopup;

        public void Initialize(
            string userId, string displayName, string instrumentSpriteName,
            bool isLocalHost, bool isSelf, LobbyMemberStage stage,
            Action onKick, Action onMakeHost,
            LobbyPlayerActionsPopup actionsPopup)
        {
            // Caller resolves the sprite name (self → local profile's CurrentInstrument; remote → fallback)
            // because the lobby contract doesn't yet expose per-member instrument data.
            if (_playerNameText != null)
            {
                _playerNameText.text = string.IsNullOrEmpty(instrumentSpriteName)
                    ? displayName
                    : $"<sprite name=\"{instrumentSpriteName}\"> {displayName}";
                // tintAllSprites makes inline <sprite> glyphs (the instrument icon)
                // pick up the text's vertex color — otherwise the icon stays at
                // its default colour and the row's stage tint only applies to the
                // name, making the status change look like a subtle font recolour
                // instead of the intended whole-row shift.
                _playerNameText.tintAllSprites = true;
                _playerNameText.color = stage switch
                {
                    LobbyMemberStage.InGame    => InGameColor,
                    LobbyMemberStage.OnResults => OnResultsColor,
                    _                          => ReadyColor,
                };
            }
            else
            {
                // Helps debug prefab redesigns where the TMP got renamed/unwired.
                YargLogger.LogWarning(
                    $"LobbyPlayer: _playerNameText is unwired — name '{displayName}' (userId={userId}) won't render. Re-link in the prefab inspector.");
            }

            _onKick       = onKick;
            _onMakeHost   = onMakeHost;
            _displayName  = displayName;
            _actionsPopup = actionsPopup;

            // Hidden when the local user isn't host (nothing useful to do)
            // or on their own row (Make Host / Kick on self are nonsensical).
            bool showHostControls = isLocalHost && !isSelf;
            if (_editButton != null)
            {
                _editButton.OnClick.RemoveAllListeners();
                _editButton.OnClick.AddListener(OpenActionsPopup);
                _editButton.gameObject.SetActive(showHostControls);
            }
        }

        private void OpenActionsPopup()
        {
            if (_actionsPopup == null)
            {
                YargLogger.LogWarning(
                    "LobbyPlayer: edit button clicked but no LobbyPlayerActionsPopup was wired — falling back to direct invocation order (make host, then kick) is unsafe; ignoring.");
                return;
            }
            _actionsPopup.Show(_displayName, _onMakeHost, _onKick);
        }
    }
}
