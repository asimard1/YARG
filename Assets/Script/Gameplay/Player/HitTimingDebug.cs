using System.Reflection;
using DG.Tweening;
using TMPro;
using UnityEngine;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Visuals;
using YARG.Helpers.Extensions;

namespace YARG.Gameplay.Player
{
    // TEMP debug overlay — Early/Perfect/Late after each hit. Rip out once done testing.
    // Attach one instance per player, wired via Init() to this player's TrackView.
    public class HitTimingDebug : MonoBehaviour
    {
        private const float HOLD_SECONDS           = 0.35f;
        private const float FADE_SECONDS           = 0.15f;
        private const float FontSizeToWidthRatio   = 0.03f; // tune to taste

        // Reflection into TrackView's private fields so we can parent to its canvas
        // and read the highway bounds without touching TrackView.cs. Both lookups
        // happen once in Init() — not per-frame — so this isn't the OnGUI cold path.
        private static readonly FieldInfo CenterContainerField =
            typeof(TrackView).GetField("_centerElementContainer", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo HighwayRendererField =
            typeof(TrackView).GetField("_highwayRenderer", BindingFlags.NonPublic | BindingFlags.Instance);

        private HighwayCameraRendering _highwayRenderer;
        private int _highwayIndex;

        private Canvas          _canvas;
        private RectTransform   _canvasRect;
        private RectTransform   _rect;
        private CanvasGroup     _canvasGroup;
        private TextMeshProUGUI _label;
        private Sequence        _sequence;

        public void Init(TrackView trackView, int highwayIndex)
        {
            _highwayIndex = highwayIndex;
            _highwayRenderer = HighwayRendererField?.GetValue(trackView) as HighwayCameraRendering;

            var centerContainer = CenterContainerField?.GetValue(trackView) as RectTransform;
            _canvas = centerContainer != null ? centerContainer.GetComponentInParent<Canvas>() : null;
            _canvasRect = _canvas != null ? _canvas.transform as RectTransform : null;

            if (_highwayRenderer == null || _canvasRect == null)
            {
                YargLogger.LogWarning("HitTimingDebug: couldn't resolve highway renderer or canvas, disabling overlay.");
                enabled = false;
                return;
            }

            var go = new GameObject("HitTimingDebugLabel", typeof(RectTransform));
            _rect = (RectTransform) go.transform;
            _rect.SetParent(_canvasRect, false);
            _rect.pivot = new Vector2(0.5f, 0.5f);

            _canvasGroup = go.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            _label = go.AddComponent<TextMeshProUGUI>();
            _label.alignment = TextAlignmentOptions.Center;
            _label.fontStyle = FontStyles.Bold;
            _label.enableWordWrapping = false;
            _label.outlineWidth = 0.2f;
            _label.outlineColor = Color.black;
        }

        public void Show(double offset, double frontEnd, double backEnd)
        {
            double window = offset < 0 ? frontEnd : backEnd;
            double perfect = window * 0.2;
            double close = window * 0.6;
            double abs = System.Math.Abs(offset);

            string text;
            Color color;
            float xOffsetFraction;

            if (abs <= perfect)
            {
                text = "Perfect";
                color = Color.green;
                xOffsetFraction = 0f;
            }
            else
            {
                text = offset < 0 ? "<<< Early" : "Late >>>";
                color = abs <= close ? new Color(0.3f, 0.6f, 1f) : Color.yellow;
                xOffsetFraction = (offset < 0 ? -0.2f : 0.2f) * (abs <= close ? 1f : 2f);
            }

            Display(text, color, xOffsetFraction);
        }

        public void ShowMiss()
        {
            Display("Miss", Color.red, 0f);
        }

        private void Display(string text, Color color, float xOffsetFraction)
        {
            if (_highwayRenderer == null)
            {
                return; // Init() failed to resolve dependencies — overlay disabled
            }

            var bounds = _highwayRenderer.GetTrackBoundsScreenSpace(_highwayIndex);
            if (bounds == null)
            {
                return;
            }

            Rect vp = bounds.Value;

            float screenFontSize = vp.width * FontSizeToWidthRatio;
            float screenBoxHeight = screenFontSize * 1.5f;
            float scaleFactor = _canvas.scaleFactor;

            _label.fontSize = screenFontSize / scaleFactor;
            _rect.sizeDelta = new Vector2(vp.width / scaleFactor, screenBoxHeight / scaleFactor);

            // Box sits just above the bottom edge of the highway bounds (near the
            // strike line), matching the original GUI.Label positioning math.
            var centerScreen = new Vector2(
                vp.x + vp.width / 2f + xOffsetFraction * (vp.width / 2f),
                vp.y + screenBoxHeight / 2f);

            var local = _canvasRect.ScreenPointToLocalPoint(centerScreen);
            if (local != null)
            {
                _rect.anchoredPosition = local.Value;
            }

            _label.text = text;
            _label.color = color;

            _sequence?.Kill();
            _canvasGroup.alpha = 1f;
            _sequence = DOTween.Sequence()
                .AppendInterval(HOLD_SECONDS)
                .Append(_canvasGroup.DOFade(0f, FADE_SECONDS))
                .SetLink(gameObject)
                .SetUpdate(true);
        }

        private void OnDestroy()
        {
            _sequence?.Kill();
        }
    }
}
