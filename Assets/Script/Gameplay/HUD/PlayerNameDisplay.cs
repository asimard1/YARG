using DG.Tweening;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Helpers.Extensions;
using YARG.Player;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public class PlayerNameDisplay : GameplayBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _playerName;
        [SerializeField]
        private Image _instrumentIcon;
        [SerializeField]
        private RawImage _needleIcon;

        private CanvasGroup _canvasGroup;
        private YargPlayer _player;

        public float DisplayTime = 3.0f;
        public float FadeDuration = 0.5f;

        // Remote names hold at partial alpha so the local user can identify each highway.
        public float RemoteHoldAlpha = 0.35f;

        protected override void GameplayAwake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
        }

        public void ShowPlayer(YargPlayer player)
        {
            if (!ShouldShowPlayer())
            {
                return;
            }

            _player = player;
            var profile = player.Profile;
            _playerName.text = profile.Name;

            var spriteName = player.GetInstrumentSprite();
            _instrumentIcon.sprite = Addressables
                .LoadAssetAsync<Sprite>(spriteName)
                .WaitForCompletion();

            StartCoroutine(FadeoutCoroutine());
        }

        public void ShowPlayer(YargPlayer player, int needleId)
        {
            if (!ShouldShowPlayer())
            {
                return;
            }

            var textureNeedle = $"VocalNeedleTexture/{needleId}";
            _needleIcon.texture = Addressables.LoadAssetAsync<Texture2D>(textureNeedle).WaitForCompletion();
            _instrumentIcon.color = player.GetHarmonyColor();
            ShowPlayer(player);
        }

        private bool ShouldShowPlayer()
        {
            return !GameManager.IsPractice && SettingsManager.Settings.ShowPlayerNameWhenStartingSong.Value;
        }

        private IEnumerator FadeoutCoroutine()
        {
            _canvasGroup.alpha = 1f;

            // Wait for loading screen to dismiss before starting the display timer.
            while (LoadingScreen.IsActive)
            {
                yield return null;
            }

            yield return new WaitForSeconds(DisplayTime);

            bool isRemote = _player != null && _player.IsRemote;
            float endAlpha = isRemote ? RemoteHoldAlpha : 0f;
            yield return _canvasGroup.DOFade(endAlpha, FadeDuration).WaitForCompletion();

            if (!isRemote)
            {
                gameObject.SetActive(false);
            }
        }
    }
}
