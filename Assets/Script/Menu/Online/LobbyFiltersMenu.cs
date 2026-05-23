using TMPro;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Settings;
using YARG.Menu.Settings.Visuals;
using YARG.Settings.Types;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Sort + filter popup for the lobby browser. Mirrors MusicLibrary's FiltersMenu
    /// structure: header → row(...) with a reset rowIndex per section so alternating
    /// backgrounds restart. Dropdowns use the common DropdownSelection prefab via
    /// <see cref="DropdownSettingVisual"/> + <see cref="DropdownSetting{T}"/>, the same
    /// path as FiltersMenu's sort dropdown.
    /// </summary>
    public class LobbyFiltersMenu : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _headerText;

        [Space]
        [SerializeField]
        private Transform _container;
        [SerializeField]
        private NavigationGroup _navGroup;
        // Section header — matches FiltersMenu._leftHeaderPrefab (label-only).
        [SerializeField]
        private RectTransform _headerPrefab;
        // Same prefabs MusicLibrary's FiltersMenu uses, so row heights and
        // alternating backgrounds stay consistent across both row types.
        [SerializeField]
        private ToggleSettingVisual _togglePrefab;
        [SerializeField]
        private DropdownSettingVisual _dropdownPrefab;

        // Owner provides the canonical SortAttribute + Filters state and
        // the refresh hook; the popup reads + writes through it so
        // changes apply live without an explicit "apply" step.
        private OnlineMenu _owner;

        public void Bind(OnlineMenu owner)
        {
            _owner = owner;
        }

        private void OnEnable()
        {
            // Toggle / dropdown rows push their own NavigationScheme via BaseSettingNavigatable
            // when focused (the focused row's GetNavigationScheme overlays this one), so
            // popup-level entries only need Back + the Up/Down between rows.
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Close, hide: true),
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
            }, true));

            if (_headerText != null)
            {
                _headerText.text = Localize.Key("Menu.Online.FiltersMenu.Header");
            }

            BuildLayout();

            // Mirror MusicLibrary's FiltersMenu.FocusLeft: SelectFirst alone
            // does NOT register this group as the active one — SelectAt sets
            // SelectedIndex before firing SetSelected(true), so the
            // OnSelectionStateChanged callback short-circuits and
            // PushNavGroupToStack never runs. Without the explicit push here
            // the NavigateUp/NavigateDown scheme entries resolve against
            // whichever group was current before the popup opened, so the
            // popup looks "frozen" — visible but unnavigable.
            if (_navGroup != null)
            {
                _navGroup.SelectFirst();
                _navGroup.PushNavGroupToStack();
            }
        }

        private void OnDisable()
        {
            // Pop ourselves off the nav stack before the parent menu's
            // navigation entries start trying to resolve through us. The
            // NavigationGroup component's own OnDisable also removes it from
            // the stack, but that fires after the scheme pop here and only
            // when the component itself disables — calling SelectLastNavGroup
            // explicitly keeps the stack tidy even if the prefab structure
            // changes so the NavigationGroup outlives this MonoBehaviour.
            if (_navGroup != null) _navGroup.SelectLastNavGroup();
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        // Mirror FiltersMenu.BuildLeftPanel: AddHeader → AddRow sequence,
        // resetting rowIndex per section so the alternating-background
        // visuals from FilterRowBackgroundVisual still alternate cleanly.
        private void BuildLayout()
        {
            // Prefab wiring guard — without _navGroup or _container we can't
            // populate anything and the previous code would NullRef on the
            // first line. Log loudly so a missing reference is obvious in
            // the editor instead of throwing through OnEnable.
            if (_navGroup == null || _container == null)
            {
                YargLogger.LogWarning(
                    "LobbyFiltersMenu: _navGroup or _container is not wired on the prefab — skipping BuildLayout");
                return;
            }

            _navGroup.ClearNavigatables();
            _container.DestroyChildren();

            if (_owner == null) return;

            var filters = _owner.Filters;
            int rowIndex;

            // ---- Options ---------------------------------------------------
            AddHeader(Localize.Key("Menu.Online.FiltersMenu.Options"));
            rowIndex = 0;
            AddDropdown("Online.SortBy", new LobbySortDropdownSetting(
                _owner.SortAttribute, v => _owner.SortAttribute = v))?.AssignIndex(rowIndex++);

            // ---- Filters ---------------------------------------------------
            AddHeader(Localize.Key("Menu.Online.FiltersMenu.Filters"));
            rowIndex = 0;
            AddToggle("Online.ShowFullLobbies", new ToggleSetting(filters.ShowFullLobbies, v =>
            {
                filters.ShowFullLobbies = v;
                _owner.RequestRefreshAfterFilterChange();
            }))?.AssignIndex(rowIndex++);
            AddToggle("Online.LanOnly", new ToggleSetting(filters.LanOnly, v =>
            {
                filters.LanOnly = v;
                _owner.RequestRefreshAfterFilterChange();
            }))?.AssignIndex(rowIndex++);
            AddDropdown("Online.GameMode", new LobbyGameModeDropdownSetting(
                filters.GameModeFilter, v =>
                {
                    filters.GameModeFilter = v;
                    _owner.RequestRefreshAfterFilterChange();
                }))?.AssignIndex(rowIndex++);
            AddDropdown("Online.Status", new LobbyStatusDropdownSetting(
                filters.StatusFilter, v =>
                {
                    filters.StatusFilter = v;
                    _owner.RequestRefreshAfterFilterChange();
                }))?.AssignIndex(rowIndex++);
        }

        // Parity with FiltersMenu.AddDropdown / AddToggle. Returns the visual so
        // the caller can AssignIndex for the alternating row background.
        private DropdownSettingVisual AddDropdown(string unlocalizedName, ISettingType setting)
        {
            if (_dropdownPrefab == null) return null;
            var visual = Instantiate(_dropdownPrefab, _container);
            visual.AssignPresetSetting(unlocalizedName, false, setting);

            var navigatable = visual.GetComponent<BaseSettingNavigatable>();
            if (navigatable != null) _navGroup.AddNavigatable(navigatable);

            return visual;
        }

        private void AddHeader(string text)
        {
            if (_headerPrefab == null) return;
            var header = Instantiate(_headerPrefab, _container);
            var tmp = header.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = text;
        }

        private ToggleSettingVisual AddToggle(string unlocalizedName, ISettingType setting)
        {
            if (_togglePrefab == null) return null;
            var visual = Instantiate(_togglePrefab, _container);
            visual.AssignPresetSetting(unlocalizedName, false, setting);

            var navigatable = visual.GetComponent<BaseSettingNavigatable>();
            if (navigatable != null) _navGroup.AddNavigatable(navigatable);

            return visual;
        }

        public void Close()
        {
            gameObject.SetActive(false);
        }
    }
}
