using UnityEngine;

namespace YARG.Gameplay.Player
{
    // TEMP debug overlay — Early/Perfect/Late after each hit. Rip out once done testing.
    public class HitTimingDebug : MonoBehaviour
    {
        private const float HideDelaySeconds = 0.5f;

        private static HitTimingDebug _instance;

        private string _text = "";
        private float _xOffset;
        private Color _color = Color.white;
        private float _hideTime;

        private GUIStyle _style;
        private bool _guiWarmedUp;

        [RuntimeInitializeOnLoadMethod]
        private static void Init()
        {
            var go = new GameObject(nameof(HitTimingDebug));
            _instance = go.AddComponent<HitTimingDebug>();
            DontDestroyOnLoad(go);
        }

        private void OnGUI()
        {
            if (!_guiWarmedUp)
            {
                WarmUpGui();
                return;
            }

            if (Time.time > _hideTime || string.IsNullOrEmpty(_text))
            {
                return;
            }

            var rectHeight = 30;
            var rectWidth = 200;
            var rect = new Rect(
                Screen.width / 2f - rectWidth / 2f + _xOffset,
                Screen.height - rectHeight,
                rectWidth,
                rectHeight);

            // Shadow
            _style.normal.textColor = Color.black;
            GUI.Label(
                new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height),
                _text,
                _style);

            // Actual text
            _style.normal.textColor = _color;
            GUI.Label(rect, _text, _style);
        }

        private void WarmUpGui()
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            // Force Unity to initialize the IMGUI text rendering path now,
            // rather than during the first note hit.
            _style.normal.textColor = Color.clear;

            GUI.Label(
                new Rect(-1000f, -1000f, 1f, 1f),
                "Warmup",
                _style);

            _guiWarmedUp = true;
        }

        public static void Show(double offset, double frontEnd, double backEnd)
        {
            if (_instance == null)
            {
                return;
            }

            _instance.SetText(offset, frontEnd, backEnd);
        }

        public static void ShowMiss()
        {
            if (_instance == null)
            {
                return;
            }

            _instance._text = "Miss";
            _instance._xOffset = 0;
            _instance._color = Color.red;
            _instance._hideTime = Time.time + HideDelaySeconds;
        }

        private void SetText(double offset, double frontEnd, double backEnd)
        {
            double window = offset < 0 ? frontEnd : backEnd;
            double perfect = window * 0.20;
            double close = window * 0.50;

            double abs = System.Math.Abs(offset);

            if (abs <= perfect)
            {
                _xOffset = 0;
                _text = "Perfect";
                _color = Color.green;
            }
            else
            {
                _text = offset < 0 ? "◀◀◀ Early" : "Late ▶▶▶";
                _xOffset = (offset < 0 ? -40 : 40) * (abs <= close ? 1f : 2f);
                _color = abs <= close
                    ? new Color(0.3f, 0.6f, 1f)
                    : Color.yellow;
            }

            _hideTime = Time.time + HideDelaySeconds;
        }
    }
}
