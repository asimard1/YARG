using System;
using System.Globalization;
using UnityEngine;
using YARG.Core.Input;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Navigation;

namespace YARG.Menu.MusicLibrary
{
    /// <summary>
    /// Popup-style song speed picker shown over the music library in lobby
    /// picker mode. Single inline input row; Red (Back) parses the typed value,
    /// writes to <see cref="GlobalVariables.State"/>.<c>SongSpeed</c> (clamped to
    /// [MinSpeedPercent, MaxSpeedPercent]), and closes.
    /// </summary>
    public class SongSpeedMenu : MonoBehaviour
    {
        public const float MinSpeedPercent = 10f;
        public const float MaxSpeedPercent = 5000f;

        // Persists across PersistentState resets (e.g. between games) so the user
        // doesn't have to re-enter their preferred speed every time they return
        // to the music library. Defaults to 1.0 (100%).
        public static float SongSpeedMultiplier { get; private set; } = 1f;

        /// <summary>Fires when the user closes the popup (regardless of whether speed changed).</summary>
        public Action OnClosed { get; set; }

        [SerializeField]
        private Transform _container;
        [SerializeField]
        private NavigationGroup _navGroup;
        [SerializeField]
        private SongSpeedMenuInput _itemInputPrefab;

        private SongSpeedMenuInput _speedRow;

        private void OnEnable()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back),
            }, true));

            BuildRows();

            if (_navGroup != null)
            {
                _navGroup.PushNavGroupToStack();
                _navGroup.SelectFirst();
            }
        }

        private void OnDisable()
        {
            if (Navigator.Instance != null) Navigator.Instance.PopScheme();
        }

        private void BuildRows()
        {
            if (_navGroup != null) _navGroup.ClearNavigatables();
            _container.DestroyChildren();
            _speedRow = null;

            _speedRow = Instantiate(_itemInputPrefab, _container);
            float currentPercent = SongSpeedMultiplier * 100f;
            _speedRow.Initialize(
                Localize.Key("Menu.MusicLibrary.SongSpeedMenu.Speed"),
                currentPercent.ToString("0", CultureInfo.InvariantCulture),
                Localize.Key("Menu.MusicLibrary.SongSpeedMenu.SpeedPlaceholder"));
            if (_navGroup != null) _navGroup.AddNavigatable(_speedRow);
        }

        public void Back()
        {
            // Apply the typed value (if parseable) on close; preserve current speed otherwise.
            string raw = _speedRow != null ? _speedRow.Text?.Trim().TrimEnd('%') : null;
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
            {
                float clampedPercent = Mathf.Clamp(percent, MinSpeedPercent, MaxSpeedPercent);
                float multiplier = clampedPercent / 100f;
                SongSpeedMultiplier = multiplier;
                GlobalVariables.State.SongSpeed = multiplier;
            }

            gameObject.SetActive(false);
            OnClosed?.Invoke();
        }
    }
}
