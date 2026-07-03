using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using YARG.Menu.Navigation;

namespace YARG.Menu.Online
{
    // Inline text-input row for the create-lobby popup, dropped in
    // alongside the ModifierItem rows. Sits in the NavigationGroup like
    // any other row -- Confirm (Green / pointer click) focuses the inner
    // TMP_InputField so the user can type without leaving the keyboard
    // navigation flow. Reading the typed value happens through
    // <see cref="Text"/>; the CreateLobbyMenu calls that at submit time.
    public class CreateLobbyMenuItemInput : NavigatableBehaviour
    {
        // Optional header label that sits above (or beside) the input
        // field -- e.g. "Lobby Name". Hidden via prefab if a row doesn't
        // need it. Caller-localized; this component just renders the
        // string it's handed.
        [SerializeField]
        private TextMeshProUGUI _header;
        [SerializeField]
        private TMP_InputField _input;

        /// <summary>Current text content (trimmed by the caller if needed).</summary>
        public string Text => _input != null ? _input.text : string.Empty;

        /// <summary>Seed the row with a header label, initial value, and
        /// placeholder. All three are localized by the caller -- this
        /// component knows nothing about the loc map. Pass null for any
        /// piece to leave it untouched (header) or empty (text fields).</summary>
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

        // Green / Confirm hands keyboard focus to the inner input field so
        // the user can type immediately. The nav group keeps this row as
        // its selected entry; pressing Green again re-selects (a no-op)
        // and the user backs out of edit mode the same way TMP normally
        // does (Esc / Tab / pointer-click elsewhere).
        public override void Confirm()
        {
            base.Confirm();
            if (_input != null)
            {
                _input.Select();
                _input.ActivateInputField();
            }
        }

        // Clicking the row directly should also focus the input -- even if
        // the click landed on the row background rather than the inner
        // input rect, the user's intent is to type.
        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            if (_input != null && !_input.isFocused)
            {
                _input.ActivateInputField();
            }
        }
    }
}
