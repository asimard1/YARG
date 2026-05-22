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
    // Host-only popup invoked from a LobbyPlayer card's edit button. Lists
    // the host actions (Make Host, Kick) for the targeted member. Reuses
    // the MusicLibrary PopupMenuItem prefab — same look + NavigatableButton
    // wiring as the rest of the menu's popups, but standalone (no
    // dependency on MusicLibraryMenu's selection state machine).
    //
    // The LobbyViewMenu prefab hosts a single inactive instance; each
    // LobbyPlayer card calls Show(...) with the target's name + action
    // callbacks. Navigation: Up/Down move between rows, Green confirms
    // the highlighted row and closes, Red closes without acting.
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
        /// Activate the popup, populate its header with <paramref name="playerName"/>,
        /// and bind the Make Host / Kick rows to the given callbacks. Either
        /// callback may be null to suppress that row (e.g. self-targeting
        /// shouldn't happen at the call site but defending here keeps the
        /// popup safe to reuse).
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
            // Guard against pop on a never-pushed scheme (e.g. domain reload
            // disables the GameObject before OnEnable ran).
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
