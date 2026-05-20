using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace YARG.Menu.Online
{
    public class LobbyPlayer : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _playerNameText;
        [SerializeField]
        private Button _kickButton;
        [SerializeField]
        private Button _makeHostButton;

        private Action _onKick;
        private Action _onMakeHost;

        public void Initialize(string userId, string displayName, bool isLocalHost, bool isSelf, Action onKick, Action onMakeHost)
        {
            _playerNameText.text = displayName;

            _onKick = onKick;
            _onMakeHost = onMakeHost;

            _kickButton.onClick.RemoveAllListeners();
            _kickButton.onClick.AddListener(InvokeKick);
            _makeHostButton.onClick.RemoveAllListeners();
            _makeHostButton.onClick.AddListener(InvokeMakeHost);

            // Host-only actions, and never on the local player's own row.
            bool showHostControls = isLocalHost && !isSelf;
            _kickButton.gameObject.SetActive(showHostControls);
            _makeHostButton.gameObject.SetActive(showHostControls);
        }

        private void InvokeKick()     => _onKick?.Invoke();
        private void InvokeMakeHost() => _onMakeHost?.Invoke();
    }
}
