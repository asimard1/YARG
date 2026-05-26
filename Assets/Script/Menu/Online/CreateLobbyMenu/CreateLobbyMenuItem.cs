using TMPro;
using UnityEngine;
using UnityEngine.Events;
using YARG.Menu.Navigation;

namespace YARG.Menu.Online
{
    // Action-row companion to ModifierItem in the create-lobby popup. The
    // popup's option groups (Game Mode / Max Players / Visibility) use
    // ModifierItem directly -- this component covers only the "Create"
    // submit row at the top of the list, which is a one-shot action
    // button rather than a toggle. Two layouts:
    //   - Initialize(header, body, action) for two-line rows (kept for
    //     potential future "cycle through values" rows; unused by the
    //     current popup but cheap to leave in place)
    //   - InitializeAction(label, action) for the green "Create" row
    public class CreateLobbyMenuItem : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _header;
        [SerializeField]
        private TextMeshProUGUI _body;

        [field: SerializeField]
        public NavigatableButton Button { get; private set; }

        /// <summary>Two-line layout: header label + body value. Used for
        /// cycling options ("Region: US East").</summary>
        public void Initialize(string header, string body, UnityAction action)
        {
            if (_header != null)
            {
                _header.gameObject.SetActive(true);
                _header.text = header;
            }
            if (_body != null)
            {
                _body.gameObject.SetActive(true);
                _body.text = body;
            }
            Button.SetOnClickEvent(action);
        }

        /// <summary>Single-line layout for action rows (e.g. "Create").
        /// Hides the header field; the visible label sits in the body slot
        /// -- the green-variant prefab routes its visible text through the
        /// body TMP, so writing the label there is what actually renders.</summary>
        public void InitializeAction(string label, UnityAction action)
        {
            if (_header != null)
            {
                _header.gameObject.SetActive(false);
            }
            if (_body != null)
            {
                _body.gameObject.SetActive(true);
                _body.text = label;
            }
            Button.SetOnClickEvent(action);
        }

        /// <summary>Update the body text in place without re-binding the
        /// click handler. Used by cycling rows after the underlying value
        /// advances, so the user sees the new value immediately.</summary>
        public void SetBody(string body)
        {
            if (_body != null) _body.text = body;
        }
    }
}
