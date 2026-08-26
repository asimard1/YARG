using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using YARG.Gameplay.Visuals;

namespace YARG.Gameplay.Player
{
    // TEMP debug overlay — Early/Perfect/Late after each hit. Rip out once done testing.
    // Attach one instance per player, wired via Init() to this player's TrackCamera.
    public class HitTimingDebug : MonoBehaviour
    {
        private const float HideDelaySeconds = 0.5f;
        private const float FontSizeToWidthRatio = 0.03f; // tune to taste

        // Reflection into HighwayCameraRendering's private camera list so we can
        // look up our own registered index without touching that file.
        private static readonly FieldInfo CamerasField =
            typeof(HighwayCameraRendering).GetField("_cameras", BindingFlags.NonPublic | BindingFlags.Instance);

        // Shared across all instances — one renderer manages every highway.
        private static HighwayCameraRendering _sharedHighwayRendering;

        private Camera _trackCamera;
        private List<Camera> _camerasList;

        private string _text = "";
        private float  _xOffsetFraction; // -1..1, fraction of half the highway width
        private Color  _color = Color.white;
        private float  _hideTime;

        private GUIStyle _style;
        private bool _guiWarmedUp;

        public void Init(Camera trackCamera)
        {
            _trackCamera = trackCamera;

            var rendering = GetHighwayRendering();
            if (rendering != null)
            {
                GetHighwayIndex(rendering);
                SetText(0, 1, 1);   // dummy call — forces JIT now
                _hideTime = 0f;     // hide it immediately, no visible flash
            }
        }

        private HighwayCameraRendering GetHighwayRendering()
        {
            if (_sharedHighwayRendering == null)
            {
                _sharedHighwayRendering = FindAnyObjectByType<HighwayCameraRendering>();
            }
            return _sharedHighwayRendering;
        }

        private int GetHighwayIndex(HighwayCameraRendering rendering)
        {
            _camerasList ??= CamerasField?.GetValue(rendering) as List<Camera>;
            return _camerasList?.IndexOf(_trackCamera) ?? -1;
        }

        private void OnGUI()
        {
            if (_trackCamera == null)
            {
                return;
            }

            if (!_guiWarmedUp)
            {
                WarmUpGui();
                return;
            }

            if (Time.time > _hideTime || string.IsNullOrEmpty(_text))
            {
                return;
            }

            var rendering = GetHighwayRendering();
            if (rendering == null)
            {
                return;
            }

            int highwayIndex = GetHighwayIndex(rendering);
            if (highwayIndex < 0)
            {
                return; // not registered yet — will self-correct once it is
            }

            var bounds = rendering.GetTrackBoundsScreenSpace(highwayIndex);
            if (bounds == null)
            {
                return;
            }

            Rect vp = bounds.Value; // already screen/GUI space (y=0 at top)

            float fontSize   = vp.width * FontSizeToWidthRatio;
            float rectHeight = fontSize * 1.5f;
            float rectWidth  = vp.width;

            _style.fontSize = Mathf.RoundToInt(fontSize);

            float centerX = vp.x + vp.width / 2f + _xOffsetFraction * (vp.width / 2f);
            float boxY = Screen.height - vp.y - rectHeight;

            var rect = new Rect(centerX - rectWidth / 2f, boxY, rectWidth, rectHeight);

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

            _style.normal.textColor = Color.clear;

            GUI.Label(new Rect(-1000f, -1000f, 300f, 30f), "Perfect", _style);
            GUI.Label(new Rect(-1000f, -1000f, 300f, 30f), "◀◀◀ Early", _style);
            GUI.Label(new Rect(-1000f, -1000f, 300f, 30f), "Late ▶▶▶", _style);
            GUI.Label(new Rect(-1000f, -1000f, 300f, 30f), "Miss", _style);

            _guiWarmedUp = true;
        }

        public void Show(double offset, double frontEnd, double backEnd)
        {
            SetText(offset, frontEnd, backEnd);
        }

        public void ShowMiss()
        {
            _text = "Miss";
            _xOffsetFraction = 0;
            _color = Color.red;
            _hideTime = Time.time + HideDelaySeconds;
        }

        private void SetText(double offset, double frontEnd, double backEnd)
        {
            double window = offset < 0 ? frontEnd : backEnd;
            double perfect = window * 0.2f;
            double close = window * 0.6f;

            double abs = System.Math.Abs(offset);

            if (abs <= perfect)
            {
                _xOffsetFraction = 0;
                _text = "Perfect";
                _color = Color.green;
            }
            else
            {
                _text = offset < 0 ? "◀◀◀ Early" : "Late ▶▶▶";
                _xOffsetFraction = (offset < 0 ? -0.2f : 0.2f) * (abs <= close ? 1f : 2f);
                _color = abs <= close
                    ? new Color(0.3f, 0.6f, 1f)
                    : Color.yellow;
            }

            _hideTime = Time.time + HideDelaySeconds;
        }
    }
}
