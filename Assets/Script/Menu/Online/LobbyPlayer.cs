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
        [SerializeField]
        private Button _kickButton;
        [SerializeField]
        private Button _makeHostButton;

        private Action _onKick;
        private Action _onMakeHost;

        public void Initialize(string userId, string displayName, string instrumentSpriteName,
            bool isLocalHost, bool isSelf, bool isBackInLobby, Action onKick, Action onMakeHost)
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

            _onKick = onKick;
            _onMakeHost = onMakeHost;

            // Host-only actions, never shown on the local player's own row.
            // Buttons may be absent in the prefab (redesign in progress) — null-guard so a
            // missing wire-up doesn't NRE and abort the whole Refresh pipeline.
            bool showHostControls = isLocalHost && !isSelf;
            if (_kickButton != null)
            {
                _kickButton.onClick.RemoveAllListeners();
                _kickButton.onClick.AddListener(InvokeKick);
                _kickButton.gameObject.SetActive(showHostControls);
            }
            if (_makeHostButton != null)
            {
                _makeHostButton.onClick.RemoveAllListeners();
                _makeHostButton.onClick.AddListener(InvokeMakeHost);
                _makeHostButton.gameObject.SetActive(showHostControls);
            }
        }

        private void InvokeKick()     => _onKick?.Invoke();
        private void InvokeMakeHost() => _onMakeHost?.Invoke();
    }
}
