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
    /// Sort + filter popup for the lobby browser. Mirrors MusicLibrary's FiltersMenu structure.
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
        // Section header (label-only).
        [SerializeField]
        private RectTransform _headerPrefab;
        // Same prefabs as MusicLibrary's FiltersMenu for visual consistency.
        [SerializeField]
        private ToggleSettingVisual _togglePrefab;
        [SerializeField]
        private DropdownSettingVisual _dropdownPrefab;

        // Owner provides the sort/filter state; changes apply live.
        private OnlineMenu _owner;

        public void Bind(OnlineMenu owner)
        {
            _owner = owner;
        }

        private void OnEnable()
        {
            // Rows push their own scheme when focused; popup only needs Back + Up/Down.
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

            // SelectFirst alone doesn't register this group as active --
            // explicit push is needed or Up/Down resolves against the parent group.
            if (_navGroup != null)
            {
                _navGroup.SelectFirst();
                _navGroup.PushNavGroupToStack();
            }
        }

        private void OnDisable()
        {
            // Pop ourselves off the nav stack before the parent menu resumes.
            if (_navGroup != null) _navGroup.SelectLastNavGroup();
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        // AddHeader + AddRow sequence; rowIndex resets per section for alternating backgrounds.
        private void BuildLayout()
        {
            // Guard against missing prefab references.
            if (_navGroup == null || _container == null)
            {
                YargLogger.LogWarning(
                    "LobbyFiltersMenu: _navGroup or _container is not wired on the prefab -- skipping BuildLayout");
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

        // Returns the visual so the caller can AssignIndex for alternating backgrounds.
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
