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

        // Remote-player names settle to a partial alpha instead of fully fading out so
        // the local user can always see who's playing what on each highway. Local
        // players still fade to 0 (their own row is unambiguous — no need to crowd the
        // HUD with their own name for the whole song).
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

            // ShowPlayer is invoked during TrackPlayer.Initialize, which runs while the
            // LoadingScreen is still covering the gameplay scene. Counting DisplayTime
            // from there would burn the visible window before the user can read the name
            // — wait for the loading screen to drop before starting the display timer.
            while (LoadingScreen.IsActive)
            {
                yield return null;
            }

            yield return new WaitForSeconds(DisplayTime);

            bool isRemote = _player != null && _player.IsRemote;
            float endAlpha = isRemote ? RemoteHoldAlpha : 0f;
            yield return _canvasGroup.DOFade(endAlpha, FadeDuration).WaitForCompletion();

            // Remote rows keep the name component active at RemoteHoldAlpha for the rest
            // of the song. Local rows tear the GameObject down so it can't accidentally
            // intercept raycasts or eat draw-call budget.
            if (!isRemote)
            {
                gameObject.SetActive(false);
            }
        }
    }
}
