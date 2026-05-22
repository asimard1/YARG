using System.Diagnostics.CodeAnalysis;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Settings;
using YARG.Helpers.Extensions;
using YARG.Core.Audio;
using YARG.Core.Song;
using YARG.Song;
using System.Threading.Tasks;

namespace YARG.Menu.Persistent
{
    public class MusicPlayer : MonoBehaviour
    {
        private static SongEntry _nowPlaying = null;
        public static SongEntry NowPlaying => _nowPlaying;

        // When non-null, NextSong always picks this song instead of a random one.
        private static SongEntry _lockedSong = null;
        public static SongEntry LockedSong => _lockedSong;

        /// <summary>
        /// Lock the player to a specific song (looped on repeat) or unlock by passing null.
        /// If the player is active and the locked song differs from what's currently playing,
        /// the swap happens immediately via <see cref="NextSong"/>. If the player isn't active
        /// (no menu music allowed in the current context, e.g. gameplay), the flag is stored
        /// and takes effect the next time the player is enabled.
        /// </summary>
        public static void SetLockedSong(SongEntry song)
        {
            if (_lockedSong == song)
            {
                return;
            }

            _lockedSong = song;

            var instance = HelpBar.Instance ? HelpBar.Instance.MusicPlayer : null;
            if (!instance)
            {
                return;
            }

            if (!instance.gameObject.activeSelf)
            {
                return;
            }

            if (song != null && song == _nowPlaying)
            {
                return;
            }

            instance.NextSong();
        }

        private object _lock = new();
        private StemMixer _mixer = null;

        [SerializeField]
        private Image _playPauseButton;
        [SerializeField]
        private TextMeshProUGUI _songText;
        [SerializeField]
        private TextMeshProUGUI _artistText;

        [Space]
        [SerializeField]
        private Sprite _playSprite;
        [SerializeField]
        private Sprite _pauseSprite;

        private async void OnEnable()
        {
            _songText.text = _artistText.text = string.Empty;

            // Wait until the loading is done
            await UniTask.WaitUntil(() => !LoadingScreen.IsActive);

            // Disable if there are no songs to play
            if (SongContainer.Count <= 0)
            {
                gameObject.SetActive(false);
                return;
            }
            StemSettings.ApplySettings = false; // ensure that MusicPlayer uses the full-volume mix
            NextSong();
        }

        private void OnDisable()
        {
            StemSettings.ApplySettings = SettingsManager.Settings.ApplyVolumesInMusicLibrary.Value; // reset to default value
            lock (_lock)
            {
                _mixer?.Dispose();
                _mixer = null;
            }
        }

        private static Task<StemMixer> _current;
        public async void NextSong()
        {
            const int MAX_TRIES = 20;
            for (int tries = 0; tries < MAX_TRIES; tries++)
            {
                SongEntry entry;
                if (_lockedSong != null)
                {
                    // Locked-song mode (e.g. lobby top-of-queue): always use this entry,
                    // skipping the dedup check so SongEnd → NextSong loops the same song.
                    entry = _lockedSong;
                }
                else
                {
                    entry = SongContainer.GetRandomSong();
                    if (entry == _nowPlaying)
                    {
                        continue;
                    }
                }
                _nowPlaying = entry;

                Task<StemMixer> task;
                lock (_lock)
                {
                    const float SPEED = 1f;
                    _current = task = Task.Run(() => entry.LoadAudio(SPEED, SettingsManager.Settings.MusicPlayerVolume.Value, SongStem.Crowd));
                }

                var mixer = await task;
                if (mixer == null)
                {
                    continue;
                }

                lock (_lock)
                {
                    if (_current != task || !gameObject.activeSelf)
                    {
                        mixer.Dispose();
                        continue;
                    }

                    _mixer?.Dispose();
                    _mixer = mixer;
                    _mixer.SongEnd += () =>
                    {
                        _mixer.Dispose();
                        _mixer = null;
                        NextSong();
                    };
                    _mixer.Play();

                    _songText.text = _nowPlaying.Name;
                    _artistText.text = _nowPlaying.Artist;
                    _playPauseButton.sprite = _pauseSprite;
                }
                return;
            }
            _nowPlaying = null;
        }

        public void UpdateVolume(double volume)
        {
            lock (_lock)
            {
                _mixer?.SetVolume(volume);
            }
        }

        public void TogglePlay()
        {
            lock (_lock)
            {
                if (_mixer == null)
                {
                    return;
                }

                if (!_mixer.IsPaused)
                {
                    _mixer.Pause();
                    _playPauseButton.sprite = _playSprite;
                }
                else
                {
                    _mixer.Play();
                    _playPauseButton.sprite = _pauseSprite;
                }
            }
        }
    }
}