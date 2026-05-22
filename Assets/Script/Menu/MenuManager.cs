using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace YARG.Menu
{
    public class MenuManager : MonoSingleton<MenuManager>
    {
        public enum Menu
        {
            None,
            MainMenu,
            MusicLibrary,
            DifficultySelect,
            Credits,
            ProfileList,
            ProfileInfo,
            History,
            Online,
            CreateLobby,
            LobbyView,
        }

        /// <summary>
        /// The values that <see cref="_lastOpenMenu"/> is allowed to be set to
        /// (not including <see cref="Menu.None"/>.
        /// </summary>
        private static readonly HashSet<Menu> _allowedLastOpenMenus = new()
        {
            Menu.MusicLibrary,
            Menu.History,
            Menu.LobbyView,
        };

        /// <summary>
        /// The menu that was last open when the menu scene gets disabled.
        /// </summary>
        private static Menu _lastOpenMenu = Menu.None;

        /// <summary>
        /// Set from outside the MenuScene to force a specific menu open on the
        /// next <see cref="Start"/>. Consumed (reset to <see cref="Menu.None"/>)
        /// once applied. Takes precedence over <see cref="_lastOpenMenu"/>.
        /// </summary>
        private static Menu _overrideOpenMenu = Menu.None;

        private Dictionary<Menu, MenuObject> _menus;

        private readonly Stack<Menu> _openMenus = new();
        private Coroutine _reactivateCoroutine;

        /// <summary>
        /// Force a specific menu to open the next time the MenuScene's
        /// <see cref="Start"/> runs. Used by callers outside the MenuScene
        /// (e.g. <see cref="ScoreScreen.ScoreScreenMenu"/> and the gameplay
        /// bail-out paths) to deterministically choose the landing menu after
        /// a scene transition, without relying on the push/pop stack history.
        /// </summary>
        public static void SetOverrideOpenMenu(Menu menu) => _overrideOpenMenu = menu;

        protected override void SingletonAwake()
        {
            // Convert to dictionary with "Menu" as key
            var children = GetComponentsInChildren<MenuObject>(true);
            _menus = children.ToDictionary(i => i.Menu, i => i);
        }

        private void Start()
        {
            if (_overrideOpenMenu != Menu.None)
            {
                var target = _overrideOpenMenu;
                _overrideOpenMenu = Menu.None;
                SetActiveMenuExclusive(target);
                return;
            }

            // Always push the main menu
            PushMenu(Menu.MainMenu);

            if (_lastOpenMenu != Menu.None)
            {
                PushMenu(_lastOpenMenu);
            }
        }

        private void OnDisable()
        {
            _lastOpenMenu = Menu.None;

            // Set the last open menu to the first instance of the allowed menu
            // Loops from top to bottom
            foreach (var menu in _openMenus)
            {
                if (_allowedLastOpenMenus.Contains(menu))
                {
                    _lastOpenMenu = menu;
                    break;
                }
            }
        }

        /// <summary>The menu currently on top of the stack, or <see cref="Menu.None"/> if none.</summary>
        public Menu CurrentMenu => _openMenus.TryPeek(out var top) ? top : Menu.None;

        public MenuObject PushMenu(Menu menu, bool setActiveImmediate = true)
        {
            bool hideOther;

            // Get the new one
            if (_menus.TryGetValue(menu, out var newMenu))
            {
                hideOther = newMenu.HideBelow;
            }
            else
            {
                throw new InvalidOperationException($"Failed to open menu {menu}.");
            }

            // Close the currently open one
            if (hideOther && _openMenus.TryPeek(out var currentMenuEnum) &&
                _menus.TryGetValue(currentMenuEnum, out var currentMenu))
            {
                currentMenu.gameObject.SetActive(false);
            }

            // Show the new one
            if (setActiveImmediate)
            {
                newMenu.gameObject.SetActive(true);
            }

            // ... and push it onto the stack
            _openMenus.Push(menu);

            return newMenu;
        }

        public void PopMenu()
        {
            //Don't pop the only remaining menu
            if (_openMenus.Count == 1)
            {
                return;
            }

            // Close the currently open one
            if (_openMenus.TryPop(out var currentMenuEnum) &&
                _menus.TryGetValue(currentMenuEnum, out var currentMenu))
            {
                currentMenu.gameObject.SetActive(false);
            }

            if (_openMenus.TryPeek(out var newMenuEnum) &&
                _menus.TryGetValue(newMenuEnum, out var newMenu))
            {
                newMenu.gameObject.SetActive(true);
            }
            else
            {
                throw new InvalidOperationException($"Failed to open menu {newMenuEnum}.");
            }
        }

        /// <summary>
        /// Pops the current top menu and pushes <paramref name="menu"/> in its place,
        /// without re-activating the menu below in between. Used for transitions like
        /// CreateLobby → LobbyView, where Back from LobbyView should skip CreateLobby
        /// and return to the menu underneath it.
        /// </summary>
        public MenuObject ReplaceMenu(Menu menu, bool setActiveImmediate = true)
        {
            // Pop the current top from the stack and hide it. Skip the usual
            // PopMenu side-effect of activating the menu below, since PushMenu
            // is about to overwrite that slot anyway.
            if (_openMenus.TryPop(out var currentMenuEnum) &&
                _menus.TryGetValue(currentMenuEnum, out var currentMenu))
            {
                currentMenu.gameObject.SetActive(false);
            }

            return PushMenu(menu, setActiveImmediate);
        }

        /// <summary>
        /// Activates <paramref name="menu"/> and deactivates every other registered
        /// menu, replacing the top of the stack with this entry. Use in flows where
        /// transitions are deterministic (online lobby navigation) and history-driven
        /// pop behavior would cause more problems than it solves.
        /// </summary>
        public MenuObject SetActiveMenuExclusive(Menu menu)
        {
            if (!_menus.TryGetValue(menu, out var target))
            {
                throw new InvalidOperationException($"Failed to open menu {menu}.");
            }

            // Two passes — deactivate every non-target menu BEFORE activating the target.
            // Unity fires OnEnable/OnDisable synchronously inside SetActive, so a single
            // interleaved loop in dictionary order can produce:
            //   incoming.OnEnable → PushScheme (lobby, picker)
            //   outgoing.OnDisable → PopScheme (lobby) — pops the picker we just pushed
            // Two-pass guarantees the outgoing menu's PopScheme runs first, leaving the
            // incoming menu free to push its scheme onto a clean top.
            foreach (var kv in _menus)
            {
                if (kv.Key != menu) kv.Value.gameObject.SetActive(false);
            }
            target.gameObject.SetActive(true);

            // Replace the top of the stack with this menu so that any legacy
            // PopMenu / OnDisable _lastOpenMenu derivation observes a consistent
            // top. Online flow doesn't rely on the stack, but other consumers do.
            if (_openMenus.Count > 0) _openMenus.Pop();
            _openMenus.Push(menu);

            return target;
        }

        /// <summary>
        /// Deactivates every registered menu and clears the stack. Use right
        /// before a scene transition out of the MenuScene to guarantee that no
        /// menu's <c>OnEnable</c> can fire (and therefore no NavigationScheme
        /// push can affect persistent UI like the menu MusicPlayer) during the
        /// async unload window.
        /// </summary>
        public void HideAllMenus()
        {
            foreach (var kv in _menus)
            {
                kv.Value.gameObject.SetActive(false);
            }
            _openMenus.Clear();
        }

        // Disables the current menu without popping it from the stack
        public void DisableCurrentMenu()
        {
            if (_openMenus.TryPeek(out var currentMenuEnum) && _menus.TryGetValue(currentMenuEnum, out var currentMenu))
            {
                currentMenu.gameObject.SetActive(false);
            }
        }

        public void ReactivateCurrentMenu(bool forceRefreshIfActive = true)
        {
            // Show the under one
            if (_openMenus.TryPeek(out var menu) && _menus.TryGetValue(menu, out var newMenu))
            {
                if (!forceRefreshIfActive && newMenu.gameObject.activeSelf)
                {
                    return;
                }

                if (_reactivateCoroutine != null)
                {
                    StopCoroutine(_reactivateCoroutine);
                    _reactivateCoroutine = null;
                }

                _reactivateCoroutine = StartCoroutine(ReactivateMenuCoroutine(newMenu.gameObject));
            }
            else
            {
                throw new InvalidOperationException($"Failed to activate menu {menu}.");
            }
        }

        private System.Collections.IEnumerator ReactivateMenuCoroutine(GameObject menuObject)
        {
            if (menuObject == null)
            {
                _reactivateCoroutine = null;
                yield break;
            }

            // Defer activation toggles to avoid SetActive during another object's OnEnable/OnDisable.
            yield return null;

            if (menuObject.activeSelf)
            {
                menuObject.SetActive(false);
                yield return null;
            }

            menuObject.SetActive(true);
            _reactivateCoroutine = null;
        }
    }
}
