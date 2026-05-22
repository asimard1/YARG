using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Filters;
using YARG.Menu.Navigation;

namespace YARG.Menu.Online
{
    /// <summary>
    /// Single-panel sort + filter popup for the lobby browser. Mirrors the
    /// LEFT-panel layout of MusicLibrary's <see cref="FiltersMenu"/> — a
    /// header label per section followed by toggle rows under it — and
    /// drops the right-panel-with-multi-select machinery since the lobby
    /// filter set is small enough that everything fits in one column.
    ///
    /// Two sections:
    ///   - Options: one <see cref="FilterEntryRow"/> per <see cref="LobbySortAttribute"/>
    ///     value, behaving as a radio group (clicking one ticks it and
    ///     unticks its siblings — sort can only be one attribute at a time).
    ///   - Filters: one <see cref="FilterEntryRow"/> per LobbyFilterSettings
    ///     boolean (multi-select; each flag is independent).
    ///
    /// Navigation matches FiltersMenu's single-group case — one
    /// <see cref="NavigationGroup"/>, Green confirms the focused row,
    /// Red closes the popup. Confirm on a FilterEntryRow flips its
    /// Toggle (same manual-flip approach FiltersMenu uses in
    /// ToggleFocusedRight).
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
        // Same prefab assets as MusicLibrary's FiltersMenu:
        //   - _headerPrefab matches FiltersMenu._leftHeaderPrefab (the
        //     plain section-label header).
        //   - _filterEntryRowPrefab matches FiltersMenu._filterEntryRowPrefab
        //     (label + count + toggle).
        [SerializeField]
        private RectTransform _headerPrefab;
        [SerializeField]
        private FilterEntryRow _filterEntryRowPrefab;

        // Owner provides the canonical SortAttribute + Filters state and
        // the refresh hook; the popup reads + writes through it so
        // changes apply live without an explicit "apply" step.
        private OnlineMenu _owner;

        // Per-row caches so radio toggling (sort) can untick siblings and
        // multi-select rows (filters) can be tagged for the confirm-flip.
        private readonly Dictionary<LobbySortAttribute, FilterEntryRow> _sortRows = new();

        public void Bind(OnlineMenu owner)
        {
            _owner = owner;
        }

        private void OnEnable()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", HandleConfirm),
                new NavigationScheme.Entry(MenuAction.Red,   "Menu.Common.Back",    Close, hide: true),
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
            _sortRows.Clear();

            if (_owner == null) return;

            // ---- Options ---------------------------------------------------
            AddHeader(Localize.Key("Menu.Online.FiltersMenu.Options"));
            int row = 0;
            foreach (LobbySortAttribute attr in Enum.GetValues(typeof(LobbySortAttribute)))
            {
                _sortRows[attr] = AddSortRow(attr, row++);
            }

            // ---- Filters ---------------------------------------------------
            AddHeader(Localize.Key("Menu.Online.FiltersMenu.Filters"));
            row = 0;
            var filters = _owner.Filters;

            AddFilterRow(
                Localize.Key("Menu.Online.FiltersMenu.ShowFullLobbies"),
                filters.ShowFullLobbies,
                v => filters.ShowFullLobbies = v,
                row++);

            AddFilterRow(
                Localize.Key("Menu.Online.FiltersMenu.LanOnly"),
                filters.LanOnly,
                v => filters.LanOnly = v,
                row++);

            AddFilterRow(
                Localize.Key("Menu.Online.FiltersMenu.ShowBand"),
                filters.ShowBand,
                v => filters.ShowBand = v,
                row++);

            AddFilterRow(
                Localize.Key("Menu.Online.FiltersMenu.ShowQuickplay"),
                filters.ShowQuickplay,
                v => filters.ShowQuickplay = v,
                row++);

            AddFilterRow(
                Localize.Key("Menu.Online.FiltersMenu.ShowSongSelect"),
                filters.ShowSongSelect,
                v => filters.ShowSongSelect = v,
                row++);

            AddFilterRow(
                Localize.Key("Menu.Online.FiltersMenu.ShowInGame"),
                filters.ShowInGame,
                v => filters.ShowInGame = v,
                row++);
        }

        private void AddHeader(string text)
        {
            if (_headerPrefab == null) return;
            var header = Instantiate(_headerPrefab, _container);
            var tmp = header.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = text;
        }

        // Sort row — behaves as part of a radio group. ToggleChanged with
        // true: write the sort attribute + untick siblings. ToggleChanged
        // with false on the active row: restore to true (radio doesn't
        // allow deselect — at least one attribute must always be selected).
        private FilterEntryRow AddSortRow(LobbySortAttribute attr, int index)
        {
            var row = Instantiate(_filterEntryRowPrefab, _container);
            _navGroup.AddNavigatable(row.gameObject);
            row.AssignIndex(index);

            bool initiallyActive = attr == _owner.SortAttribute;
            row.Bind(FormatSort(attr), string.Empty, initiallyActive);

            row.ToggleChanged += newOn =>
            {
                if (newOn)
                {
                    _owner.SortAttribute = attr;
                    foreach (var kv in _sortRows)
                    {
                        if (kv.Key != attr) kv.Value.SetToggleIsOn(false);
                    }
                }
                else if (attr == _owner.SortAttribute)
                {
                    // User clicked the currently-active sort row, which
                    // would leave the group with no selection. Bounce it
                    // back to on — the SortAttribute setter is idempotent
                    // so no extra refresh fires.
                    row.SetToggleIsOn(true);
                }
            };
            return row;
        }

        private FilterEntryRow AddFilterRow(string label, bool initialOn, Action<bool> setter, int index)
        {
            var row = Instantiate(_filterEntryRowPrefab, _container);
            _navGroup.AddNavigatable(row.gameObject);
            row.AssignIndex(index);

            row.Bind(label, string.Empty, initialOn);
            row.ToggleChanged += value =>
            {
                setter(value);
                _owner.RequestRefreshAfterFilterChange();
            };
            return row;
        }

        // Green on a focused FilterEntryRow flips its toggle — same manual
        // flip MusicLibrary's FiltersMenu uses in ToggleFocusedRight,
        // since FilterEntryRow itself doesn't override Confirm().
        private void HandleConfirm()
        {
            if (_navGroup.SelectedBehaviour == null) return;
            var row = _navGroup.SelectedBehaviour.GetComponent<FilterEntryRow>();
            if (row?.Toggle == null) return;
            row.Toggle.isOn = !row.Toggle.isOn;
        }

        private static string FormatSort(LobbySortAttribute attr) => attr switch
        {
            LobbySortAttribute.LobbyName   => Localize.Key("Menu.Online.FiltersMenu.SortBy.LobbyName"),
            LobbySortAttribute.HostName    => Localize.Key("Menu.Online.FiltersMenu.SortBy.HostName"),
            LobbySortAttribute.SongName    => Localize.Key("Menu.Online.FiltersMenu.SortBy.SongName"),
            LobbySortAttribute.PlayerCount => Localize.Key("Menu.Online.FiltersMenu.SortBy.PlayerCount"),
            _                              => attr.ToString(),
        };

        public void Close()
        {
            gameObject.SetActive(false);
        }
    }
}
