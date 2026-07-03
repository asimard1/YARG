using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using YARG.Core.Input;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;

namespace YARG.Menu.Online
{
    // Host-only popup for Make Host / Kick actions on a targeted member.
    // LobbyViewMenu hosts a single inactive instance; each player card calls Show().
    public class LobbyPlayerActionsPopup : MonoBehaviour
    {
        [SerializeField]
        private PopupMenuItem _menuItemPrefab;

        [SerializeField]
        private GameObject _header;
        [SerializeField]
        private TextMeshProUGUI _headerText;
        [SerializeField]
        private Transform _container;
        [SerializeField]
        private NavigationGroup _navGroup;

        private Action _onMakeHost;
        private Action _onKick;

        /// <summary>
        /// Show the popup for <paramref name="playerName"/>. Null callbacks suppress that row.
        /// </summary>
        public void Show(string playerName, Action onMakeHost, Action onKick)
        {
            _onMakeHost = onMakeHost;
            _onKick     = onKick;

            SetHeader(playerName);

            _navGroup.ClearNavigatables();
            _container.DestroyChildren();

            if (_onMakeHost != null)
            {
                CreateItem(Localize.Key("Menu.Online.PlayerActions.MakeHost"), () =>
                {
                    var cb = _onMakeHost;
                    Close();
                    cb?.Invoke();
                });
            }
            if (_onKick != null)
            {
                CreateItem(Localize.Key("Menu.Online.PlayerActions.Kick"), () =>
                {
                    var cb = _onKick;
                    Close();
                    cb?.Invoke();
                });
            }

            // Show + push nav scheme via OnEnable.
            gameObject.SetActive(true);
            _navGroup.SelectFirst();
        }

        private void OnEnable()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Close),
            }, false));
        }

        private void OnDisable()
        {
            // Guard against pop on a never-pushed scheme.
            if (Navigator.Instance != null)
            {
                Navigator.Instance.PopScheme();
            }
        }

        private void Close()
        {
            gameObject.SetActive(false);
            _onMakeHost = null;
            _onKick     = null;
        }

        private void SetHeader(string text)
        {
            if (_header == null) return;
            if (string.IsNullOrEmpty(text))
            {
                _header.SetActive(false);
            }
            else
            {
                _header.SetActive(true);
                if (_headerText != null) _headerText.text = text;
            }
        }

        private void CreateItem(string body, UnityAction action)
        {
            var btn = Instantiate(_menuItemPrefab, _container);
            btn.Initialize(body, action);
            _navGroup.AddNavigatable(btn.Button);
        }
    }
}
