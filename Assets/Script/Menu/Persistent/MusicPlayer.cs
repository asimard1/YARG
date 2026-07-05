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

        // Playback speed for the currently locked song. 1f when unlocked.
        private static float _lockedSongSpeed = 1f;
        // Speed the currently-playing mixer was loaded at, so we can detect
        // a speed change on a re-lock to the same song.
        private static float _nowPlayingSpeed = 1f;

        /// <summary>
        /// Lock the player to a specific song (looped on repeat) at the supplied
        /// speed, or unlock by passing null. If the player is active and the locked
        /// song/speed differs from what's currently playing, the swap happens
        /// immediately via <see cref="NextSong"/>. If the player isn't active
        /// (no menu music allowed in the current context, e.g. gameplay), the flag
        /// is stored and takes effect the next time the player is enabled.
        /// </summary>
        public static void SetLockedSong(SongEntry song, float speed = 1f)
        {
            if (_lockedSong == song && Mathf.Approximately(_lockedSongSpeed, speed))
            {
                return;
            }

            _lockedSong = song;
            _lockedSongSpeed = speed;

            var instance = HelpBar.Instance ? HelpBar.Instance.MusicPlayer : null;
            if (!instance)
            {
                return;
            }

            if (!instance.gameObject.activeSelf)
            {
                return;
            }

            // Already playing this exact (song, speed)
            if (song != null && song == _nowPlaying && Mathf.Approximately(_nowPlayingSpeed, speed))
            {
                return;
            }

            if (song != null)
            {
                instance.NextSong();
            }
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

            // Only the Menu scene runs the rotating preview player
            if (GlobalVariables.Instance == null
                || GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                gameObject.SetActive(false);
                return;
            }

            // Disable if there are no songs to play
            if (SongContainer.Count <= 0)
            {
                gameObject.SetActive(false);
                return;
            }
            StemSettings.ApplySettings = SettingsManager.Settings.ApplyVolumesInMusicPlayer.Value;
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
                float speed;
                if (_lockedSong != null)
                {
                    entry = _lockedSong;
                    speed = _lockedSongSpeed;
                }
                else
                {
                    entry = SongContainer.GetRandomSong();
                    if (entry == _nowPlaying)
                    {
                        continue;
                    }
                    speed = 1f;
                }
                _nowPlaying = entry;
                _nowPlayingSpeed = speed;

                Task<StemMixer> task;
                lock (_lock)
                {
                    float audioSpeed = speed;
                    _current = task = Task.Run(() => entry.LoadAudio(audioSpeed, SettingsManager.Settings.MusicPlayerVolume.Value, SongStem.Crowd));
                }

                var mixer = await task;
                if (mixer == null)
                {
                    continue;
                }

                lock (_lock)
                {
                    // A LoadAudio task started while the user was still in the menu can complete after they've
                    // already entered gameplay
                    var sceneIsMenu = GlobalVariables.Instance != null
                                       && GlobalVariables.Instance.CurrentScene == SceneIndex.Menu;
                    if (_current != task || !gameObject.activeSelf || !sceneIsMenu)
                    {
                        mixer.Dispose();
                        continue;
                    }

                    _mixer?.Dispose();
                    _mixer = mixer;
                    // Capture the mixer locally so SongEnd can't fire on a
                    // later-swapped-in mixer (e.g. user re-locked mid-song,
                    // scene swap raced the playback loop).
                    var endedMixer = mixer;
                    endedMixer.SongEnd += () =>
                    {
                        endedMixer.Dispose();
                        if (_mixer == endedMixer)
                        {
                            _mixer = null;
                        }
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