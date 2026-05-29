using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using YARG.Menu.Navigation;

namespace YARG.Menu.MusicLibrary
{
    /// <summary>
    /// Inline numeric-input row for the song speed popup. Sits in a NavigationGroup;
    /// Confirm (Green / pointer click) focuses the inner TMP_InputField so the user
    /// can type. Read the typed value via <see cref="Text"/>. The input field's
    /// content type should be set to Decimal in the prefab to restrict keyboard input.
    /// On end-edit (Enter or focus loss) the typed value is parsed and clamped to
    /// [<see cref="SongSpeedMenu.MinSpeedPercent"/>, <see cref="SongSpeedMenu.MaxSpeedPercent"/>],
    /// matching the DifficultySelect speed input's behavior.
    /// </summary>
    public class SongSpeedMenuInput : NavigatableBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _header;
        [SerializeField]
        private TMP_InputField _input;

        /// <summary>Current text content (caller trims and parses).</summary>
        public string Text => _input != null ? _input.text : string.Empty;

        private void OnEnable()
        {
            if (_input != null)
            {
                _input.onEndEdit.AddListener(OnEndEdit);
            }
        }

        private void OnDisable()
        {
            if (_input != null)
            {
                _input.onEndEdit.RemoveListener(OnEndEdit);
            }
        }

        /// <summary>Seed the row with a header label, initial value, and placeholder.</summary>
        public void Initialize(string headerText, string initialText, string placeholderText)
        {
            if (_header != null && headerText != null)
            {
                _header.gameObject.SetActive(true);
                _header.text = headerText;
            }
            if (_input == null) return;
            _input.text = initialText ?? string.Empty;
            if (_input.placeholder is TextMeshProUGUI placeholderTmp && placeholderText != null)
            {
                placeholderTmp.text = placeholderText;
            }
        }

        public override void Confirm()
        {
            base.Confirm();
            if (_input != null)
            {
                _input.Select();
                _input.ActivateInputField();
            }
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            if (_input != null && !_input.isFocused)
            {
                _input.ActivateInputField();
            }
        }

        // Mirrors DifficultySelectMenu.SongSpeedEndEdit: unparseable falls back to
        // 100%, then the value is clamped to the [min, max] range and rewritten
        // (SetTextWithoutNotify) so the user sees the clamped value immediately.
        private void OnEndEdit(string text)
        {
            if (!float.TryParse(text?.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float speed))
            {
                speed = 100f;
            }

            int intSpeed = (int) Math.Clamp(speed, SongSpeedMenu.MinSpeedPercent, SongSpeedMenu.MaxSpeedPercent);
            _input.SetTextWithoutNotify($"{intSpeed}%");
        }
    }
}
