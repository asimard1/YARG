using TMPro;
using UnityEngine;
using YARG.Online.Lobbies.Contracts.Hubs;

namespace YARG.Menu.Online
{
    public class ChatMessageCard : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _text;

        public void Initialize(ChatMessage message)
        {
            // SentAt is UTC from the server (LobbyHub _clock.GetUtcNow()).
            string time = message.SentAt.ToLocalTime().ToString("HH:mm");

            // Defang any '<' from user-supplied fields so a malicious name/body
            // can't break out into TMP rich-text markup.
            string name = message.DisplayName?.Replace("<", "<​") ?? string.Empty;
            string body = message.Text?.Replace("<", "<​") ?? string.Empty;

            _text.text = $"<color=#888>[{time}]</color> <b>{name}:</b> {body}";
        }
    }
}
