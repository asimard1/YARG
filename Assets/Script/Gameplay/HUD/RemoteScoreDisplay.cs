using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using YARG.Gameplay.Player;
using YARG.Gameplay.Visuals;

namespace YARG.Gameplay.HUD
{
    // One row per remote player when ShowRemoteHighways is off. Reads score state
    // directly off the remote's BasePlayer (engine is still ticking even when the
    // highway is hidden -- see GameManager.Loading.cs). Mirrors the combo / FC /
    // multiplier model updates that TrackPlayer.UpdateVisuals does.
    public class RemoteScoreDisplay : MonoBehaviour
    {
        [SerializeField]
        private Image _instrumentIcon;
        [SerializeField]
        private TextMeshProUGUI _playerNameText;

        [Header("Combo meter (3D models on a render-texture rig)")]
        [SerializeField]
        private ComboMeter _comboMeter;

        // Root of the 3D models + camera that feeds this row's render texture.
        // Each spawned instance is shifted along world X so the rigs don't
        // overlap and the cameras only see their own slot.
        [SerializeField]
        private Transform _renderRig;
        [SerializeField]
        private float _rigSlotSpacing = 100f;

        // The prefab references a shared RenderTexture asset on both the camera's
        // targetTexture and the RawImage's texture. Without cloning at runtime,
        // every spawned instance writes to the same RT and only the last camera
        // to render per frame wins. Wire the prefab's camera + display image here
        // and we hand each instance its own RT clone in Initialize.
        [SerializeField]
        private Camera _renderCamera;
        [SerializeField]
        private RawImage _renderDisplay;

        private RenderTexture _instanceTexture;

        [Header("Star Power")]
        [SerializeField]
        private Color _starPowerNameColor = new(1f, 0.85f, 0.2f);

        private BasePlayer _player;
        private bool _isBandPlay;
        private Color _baseNameColor;
        private bool _starPowerWasActive;
        private bool _lastFcSeen = true;
        private bool _isVisible = true;

        public void Initialize(BasePlayer player, bool isBandPlay, int slotIndex)
        {
            _player = player;
            _isBandPlay = isBandPlay;

            // Shift the 3D rig so each instance owns its own slice of world
            // space; the prefab's camera (child of _renderRig) moves with it
            // and only sees this slot's models.
            if (_renderRig != null && slotIndex > 0)
            {
                _renderRig.position += Vector3.right * (slotIndex * _rigSlotSpacing);
            }

            // Give this instance its own RenderTexture so the camera isn't
            // writing into a shared asset that every other instance reads from.
            if (_renderCamera != null && _renderCamera.targetTexture != null)
            {
                var template = _renderCamera.targetTexture;
                _instanceTexture = new RenderTexture(template.descriptor)
                {
                    name = $"{template.name}_remote_slot{slotIndex}",
                };
                _renderCamera.targetTexture = _instanceTexture;
                if (_renderDisplay != null)
                {
                    _renderDisplay.texture = _instanceTexture;
                }
            }

            var profile = player.Player.Profile;
            _playerNameText.text = profile.Name;
            _baseNameColor = _playerNameText.color;

            var spriteName = player.Player.GetInstrumentSprite();
            if (_instrumentIcon != null && !string.IsNullOrEmpty(spriteName))
            {
                _instrumentIcon.sprite = Addressables
                    .LoadAssetAsync<Sprite>(spriteName)
                    .WaitForCompletion();
            }

            if (_comboMeter != null)
            {
                _comboMeter.Initialize(player.Player.EnginePreset, player.BaseParameters.MaxMultiplier, isBandPlay);
                _comboMeter.SetFullCombo(player.IsFc);
            }
            _lastFcSeen = player.IsFc;
        }

        private void Update()
        {
            if (_player == null) return;

            // Remote disconnected mid-song: stop drawing for them.
            if (_player.HasLeftGame)
            {
                if (_isVisible)
                {
                    _isVisible = false;
                    gameObject.SetActive(false);
                }
                return;
            }

            var stats = _player.BaseStats;

            int maxMultiplier = _player.BaseParameters.MaxMultiplier;
            if (stats.IsStarPowerActive)
            {
                maxMultiplier *= 2;
            }

            // Mirrors TrackPlayer: in band play, halve the readout while SP is
            // active so the band-side x2 doesn't look like a double-count.
            int displayMultiplier = _isBandPlay && stats.IsStarPowerActive
                ? stats.ScoreMultiplier / 2
                : stats.ScoreMultiplier;

            if (_comboMeter != null)
            {
                _comboMeter.SetCombo(
                    stats.ScoreMultiplier,
                    displayMultiplier,
                    maxMultiplier,
                    stats.Combo,
                    _player.BaseEngine.CodaHasStarted);
            }

            // FC ring tracks BasePlayer.IsFc; the engine flips it on miss/overhit
            // for both local and remote players, so polling here is sufficient.
            if (_player.IsFc != _lastFcSeen)
            {
                _lastFcSeen = _player.IsFc;
                _comboMeter?.SetFullCombo(_lastFcSeen);
            }

            if (stats.IsStarPowerActive != _starPowerWasActive)
            {
                _starPowerWasActive = stats.IsStarPowerActive;
                ApplyStarPowerNameVisual(stats.IsStarPowerActive);
            }
        }

        private void ApplyStarPowerNameVisual(bool active)
        {
            if (_playerNameText == null) return;
            _playerNameText.color = active ? _starPowerNameColor : _baseNameColor;
        }

        private void OnDestroy()
        {
            if (_instanceTexture != null)
            {
                if (_renderCamera != null && _renderCamera.targetTexture == _instanceTexture)
                {
                    _renderCamera.targetTexture = null;
                }
                _instanceTexture.Release();
                Destroy(_instanceTexture);
                _instanceTexture = null;
            }
        }
    }
}
