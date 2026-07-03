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
        // Stage → name tint: White = InLobby, Yellow = InGame, Orange = OnResults.
        private static readonly Color ReadyColor    = Color.white;
        private static readonly Color InGameColor   = new(1f, 0.85f, 0.2f, 1f);
        private static readonly Color OnResultsColor = new(1f, 0.55f, 0.15f, 1f);

        [SerializeField]
        private TextMeshProUGUI _playerNameText;

        // Host-action button. Hidden for non-hosts and the host's own row.
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
            if (_playerNameText != null)
            {
                _playerNameText.text = string.IsNullOrEmpty(instrumentSpriteName)
                    ? displayName
                    : $"<sprite name=\"{instrumentSpriteName}\"> {displayName}";
                // tintAllSprites so the instrument icon picks up the stage colour too.
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
                YargLogger.LogWarning(
                    $"LobbyPlayer: _playerNameText is unwired -- name '{displayName}' (userId={userId}) won't render. Re-link in the prefab inspector.");
            }

            _onKick       = onKick;
            _onMakeHost   = onMakeHost;
            _displayName  = displayName;
            _actionsPopup = actionsPopup;

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
                    "LobbyPlayer: edit button clicked but no LobbyPlayerActionsPopup was wired -- falling back to direct invocation order (make host, then kick) is unsafe; ignoring.");
                return;
            }
            _actionsPopup.Show(_displayName, _onMakeHost, _onKick);
        }
    }
}
