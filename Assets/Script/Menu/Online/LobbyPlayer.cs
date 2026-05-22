using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Logging;

namespace YARG.Menu.Online
{
    public class LobbyPlayer : MonoBehaviour
    {
        // White = back in the lobby (song-select), ready to start.
        // Yellow = mid-game / on the results screen — the host's Start
        // button is gated on every member's flag flipping back to white.
        private static readonly Color ReadyColor   = Color.white;
        private static readonly Color InGameColor  = new(1f, 0.85f, 0.2f, 1f);

        [SerializeField]
        private TextMeshProUGUI _playerNameText;

        // Single host-action entry point. Hidden for non-hosts and for the
        // host's own row (you can't kick yourself / re-host yourself).
        // Clicking opens a shared LobbyPlayerActionsPopup that lists Make
        // Host + Kick targeting this row's member.
        [SerializeField]
        private Button _editButton;

        // Legacy kick/host buttons from the previous LobbyPlayer prefab —
        // kept for compatibility while the prefab is being updated to use
        // the EditButton + popup flow. Always hidden when an EditButton is
        // present so a half-updated prefab doesn't expose two parallel
        // paths to the same action. Once the prefab drops these, the
        // SerializeFields + null-guards can be deleted with no behavior
        // change.
        [SerializeField]
        private Button _kickButton;
        [SerializeField]
        private Button _makeHostButton;

        private Action _onKick;
        private Action _onMakeHost;
        private string _displayName;
        private LobbyPlayerActionsPopup _actionsPopup;

        public void Initialize(
            string userId, string displayName, string instrumentSpriteName,
            bool isLocalHost, bool isSelf, bool isBackInLobby,
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
                _playerNameText.color = isBackInLobby ? ReadyColor : InGameColor;
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

            // EditButton is the new single entry point for host actions on
            // other members. Hidden when the local user isn't host (nothing
            // useful for them to do here) or when looking at their own row
            // (Make Host / Kick on self are nonsensical operations).
            bool showHostControls = isLocalHost && !isSelf;
            if (_editButton != null)
            {
                _editButton.onClick.RemoveAllListeners();
                _editButton.onClick.AddListener(OpenActionsPopup);
                _editButton.gameObject.SetActive(showHostControls);
            }

            // Legacy direct-action buttons: always hide if the prefab still
            // has them. The EditButton + popup is the only sanctioned UI
            // surface for host actions now.
            if (_kickButton != null)
            {
                _kickButton.onClick.RemoveAllListeners();
                _kickButton.gameObject.SetActive(false);
            }
            if (_makeHostButton != null)
            {
                _makeHostButton.onClick.RemoveAllListeners();
                _makeHostButton.gameObject.SetActive(false);
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
