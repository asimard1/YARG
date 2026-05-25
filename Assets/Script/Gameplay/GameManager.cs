using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Core.Replays.Analyzer;
using YARG.Core.Song;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Input;
using YARG.Integration;
using YARG.Localization;
using YARG.Menu;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.ScoreScreen;
using YARG.Online;
using YARG.Playback;
using YARG.Player;
using YARG.Replays;
using YARG.Scores;
using YARG.Settings;
using YARG.Venue.Characters;
using YARG.Venue.VenueCamera;

namespace YARG.Gameplay
{
    [DefaultExecutionOrder(-1)]
    public partial class GameManager : MonoBehaviour
    {
        public const double SONG_START_DELAY = SongRunner.SONG_START_DELAY;
        public const double SONG_END_DELAY = SONG_START_DELAY;

        public const double PAUSE_REWIND_LENGTH   = 1;
        public const double MAXIMUM_REWIND_TIME   = 3;
        public const double MAXIMUM_REWIND_WINDOW = 20;

        public const float TRACK_SPACING_X = 100f;


        public bool IsSeekingReplay;

        [Header("References")]
        [SerializeField]
        private TrackViewManager _trackViewManager;
        [SerializeField]
        private ReplayController _replayController;
        [SerializeField]
        private PauseMenuManager _pauseMenu;
        [SerializeField]
        private DraggableHudManager _draggableHud;

        [SerializeField]
        private LyricBar _lyricBar;

        [SerializeField]
        private FailMeter _failMeter;

        [SerializeField]
        private BREBox _breBox;

        [field: SerializeField]
        public VocalTrack VocalTrack { get; private set; }

        /// <summary>
        /// Equal to either <see cref="PlayerContainer.Players"/> or the players in the replay.
        /// </summary>
        public IReadOnlyList<YargPlayer> YargPlayers { get; private set;}

        public static bool IsOnline { get; set; }

        public OnlineSessionDirector OnlineSession { get; private set; }

        private List<BasePlayer> _players;

        public int TotalPlayers => _players.Count;

        public bool IsSongReady { get; private set; } = false;

        public bool IsSongStarted { get; private set; } = false;

        private SongRunner _songRunner;

        /// <remarks>
        /// This is not initialized on awake, but rather, in
        /// <see cref="GameplayBehaviour.OnChartLoaded"/>.
        /// </remarks>
        public BeatEventHandler BeatEventHandler { get;    private set; }
        public CrowdEventHandler CrowdEventHandler  { get; private set; }
        public CameraManager     VenueCameraManager { get; private set; }
        public CharacterManager  VenueCharacterManager { get; private set; }

        public PracticeManager  PracticeManager  { get; private set; }
        public BackgroundManager BackgroundManager { get; private set; }
        public EngineManager EngineManager { get; private set; }

        public SongEntry Song  { get; private set; }
        public SongChart    Chart { get; private set; }

        // For clarity, try to avoid using these properties inside GameManager itself
        // These are just to expose properties from the song runner to the outside
        /// <inheritdoc cref="SongRunner.SongTime"/>
        public double SongTime => _songRunner.SongTime;

        /// <inheritdoc cref="SongRunner.AudioTime"/>
        public double AudioTime => _songRunner.AudioTime;

        /// <inheritdoc cref="SongRunner.VisualTime"/>
        public double VisualTime => _songRunner.VisualTime;

        /// <inheritdoc cref="SongRunner.InputTime"/>
        public double InputTime => _songRunner.InputTime;

        /// <inheritdoc cref="SongRunner.SongSpeed"/>
        public float SongSpeed => _songRunner.SongSpeed;

        /// <inheritdoc cref="SongRunner.Started"/>
        public bool Started => _songRunner.Started;

        /// <inheritdoc cref="SongRunner.Paused"/>
        public bool Paused => _songRunner.Paused;

        /// <summary>
        /// Set when we are in the middle of resuming, but have not yet fully resumed
        /// </summary>
        public bool Rewinding { get; private set; }

        public double SongLength { get; private set; }

        public bool IsPractice      { get; private set; }

        public bool IsReplay => ReplayInfo != null && !GlobalVariables.State.PlayingWithReplay;

        public int BandScore
        {
            get => EngineManager.Score;
            set => EngineManager.Score = value;
        }

        public int BandCombo
        {
            get => EngineManager.Combo;
            set => EngineManager.Combo = value;
        }

        public float BandStars => EngineManager.Stars;

        public int BandMultiplier => EngineManager.BandMultiplier;

        public double FirstNoteTime { get; private set; }
        public double LastNoteTime  { get; private set; }

        public ReplayInfo ReplayInfo { get; private set; }
        public ReplayData ReplayData { get; private set; }

        public List<PauseInfo> PauseInfo { get; } = new List<PauseInfo>();

        public IReadOnlyList<BasePlayer> Players => _players;

        public int StarPowerActivations { get; private set; } = 0;

        private bool _isReplaySaved;
        private int _originalSleepTimeout;
        private bool _breBoxActive;
        private bool _gameCompleteSent;
        private double _gameCompleteRealtime;
        // Upper bound on how long EndSong waits for every remote peer's
        // final EngineStateSnapshot before falling back to the results
        // screen. 3s is comfortable under typical network conditions and
        // short enough that a disconnected straggler doesn't pin players
        // at the end-of-song screen.
        private const double FINAL_SNAPSHOT_TIMEOUT_SECONDS = 3.0;

        private StemMixer _mixer;

        private List<double> _frameTimes;

        private double _pauseTime;
        private double _rewindLimit = double.MinValue;
        private bool   _resumeInProgress;
        private bool   _autoCalibrateVideoOnPause;
        private double _preFadeOutVolume = DEFAULT_VOLUME;

        public bool PlayingAShow => GlobalVariables.State.PlayingAShow;
        public int  ShowIndex = 0;

        private BandComboType _bandComboType;

        private        bool HasBots            => _players.Any(p => !p.Player.SittingOut && p.Player.Profile.IsBot);
        private static bool SaveScoresWithBots => SettingsManager.Settings.SaveScoresWithBots.Value;

        private void Awake()
        {
            // Set references
            PracticeManager = GetComponent<PracticeManager>();
            BackgroundManager = GetComponent<BackgroundManager>();
            EngineManager = new EngineManager();
            if (IsOnline)
            {
                EngineManager.BandFeaturesEnabled = false;
            }

            if (IsOnline)
            {
                OnlineSession = OnlineSessionDirector.Current;
                if (OnlineSession == null)
                {
                    YargLogger.LogError(
                        "GameManager: IsOnline is true but OnlineSessionDirector.Current is null; " +
                        "aborting back to menu.");
                    BailOutToMenu();
                    return;
                }
                YargPlayers = OnlineSession.Players;
                // Hide a peer's highway on RemotePeerLeft — fires when their
                // UDP session drops (either explicit Quit via
                // LobbyHubSession.LeaveCurrentGame, or a network failure).
                // Without this the track keeps rendering empty.
                OnlineSession.RemotePlayerLeft += HideRemotePlayerHighway;

                // If the session is already dead by the time we attach (e.g.
                // GameEnd raced ahead of GameManager.Start) we still want to
                // bail rather than play through. Otherwise we wire the live
                // signal and let the next external death route us out.
                if (OnlineSession.SessionAbortedExternally)
                {
                    YargLogger.LogWarning(
                        "GameManager: OnlineSession already aborted before Start subscribed — bailing.");
                    BailOutToMenu();
                    return;
                }
                OnlineSession.SessionEndedExternally += OnOnlineSessionEnded;
            }
            else
            {
                YargPlayers = PlayerContainer.Players;
            }

            Song = GlobalVariables.State.CurrentSong;
            ReplayInfo = GlobalVariables.State.CurrentReplay;
            IsPractice = GlobalVariables.State.IsPractice && ReplayInfo == null;
            _bandComboType = SettingsManager.Settings.BandComboTypeSetting.Value;

            Navigator.Instance.PopAllSchemes();
            GameStateFetcher.SetSongEntry(Song);

            if (Song is null)
            {
                YargLogger.LogError("Null song set when loading gameplay!");

                BailOutToMenu();
                return;
            }

            // Hide vocals track (will be shown when players are initialized)
            VocalTrack.gameObject.SetActive(false);

            // Prevent screen from sleeping
            _originalSleepTimeout = Screen.sleepTimeout;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            // Update countdown display style from global settings
            CountdownDisplay.DisplayStyle = SettingsManager.Settings.CountdownDisplay.Value;

            _frameTimes = new List<double>();
        }

        private void OnDestroy()
        {
            YargLogger.LogInfo("Exiting song");

            if (Navigator.Instance != null)
            {
                Navigator.Instance.NavigationEvent -= OnNavigationEvent;
            }

            // Unsubscribe from other events
            SettingsManager.Settings.NoFail.OnChange -= OnNoFailModeChanged;
            EngineManager.OnSongFailed -= OnSongFailed;
            EngineManager.OnCodaStart -= StartCoda;
            EngineManager.OnCodaEnd -= EndCoda;

            //Restore stem volumes to their original state
            foreach (var (stem, state) in _stemStates)
            {
                GlobalAudioHandler.SetVolumeSetting(stem, state.Volume);
            }

            DisposeDebug();
            _pauseMenu.PopAllMenus();
            _mixer?.Dispose();
            _songRunner?.Dispose();
            BackgroundManager.Dispose();
            CrowdEventHandler.Dispose();

            // Reset the time scale back, as it would be 0 at this point (because of pausing)
            Time.timeScale = 1f;

            // Reset sleep timeout setting
            Screen.sleepTimeout = _originalSleepTimeout;

            if (OnlineSession != null)
            {
                OnlineSession.RemotePlayerLeft -= HideRemotePlayerHighway;
                OnlineSession.SessionEndedExternally -= OnOnlineSessionEnded;
            }

            // Clear the online flag.
            // The orchestrator sets this back to true before each online scene load.
            IsOnline = false;
        }

        // Find the BasePlayer mirroring the given remote peer and disable its
        // GameObject so the highway, score box, and per-frame Update logic
        // all stop. _players still contains the entry (the results screen
        // iterates it) but with the GameObject inactive nothing renders.
        private void HideRemotePlayerHighway(int peerId)
        {
            if (_players == null) return;
            foreach (var player in _players)
            {
                if (player == null) continue;
                if (player.Player == null) continue;
                if (!player.Player.IsRemote) continue;
                if (player.Player.RemotePeerId != peerId) continue;

                YargLogger.LogInfo(
                    $"GameManager: hiding remote player highway for peerId={peerId} ({player.Player.Profile?.Name})");
                player.gameObject.SetActive(false);

                // Vocals' player HUD card (name + score + needle icon) lives under a
                // shared HUD canvas, not as a child of the player transform, so the
                // SetActive above doesn't reach it. Tear it down explicitly so a
                // vocalist who quits mid-song actually disappears from the screen.
                if (player is VocalsPlayer vocals)
                {
                    vocals.HideHud();
                    // If the leaver was the last active vocalist, demote the vocal
                    // highway to the lyric bar so the remaining instrument players
                    // aren't staring at an empty vocal track for the rest of the song.
                    TrySwitchToLyricBarIfNoVocalists();
                }
                return;
            }
        }

        // Walks _players for any still-active VocalsPlayer (local or remote). When
        // none remain — i.e. every vocalist either never existed or has been hidden
        // via HideRemotePlayerHighway — disables the vocal highway and falls back
        // to the lyric bar at the bottom of the screen, mirroring the load-time
        // selection at GameManager.Loading.cs:580/592. The bar's phrase objects
        // were already instantiated under it during OnChartLoaded (which fires
        // regardless of active state), so a SetActive(true) + SetSongTime is
        // enough to bring it up to date mid-song.
        private void TrySwitchToLyricBarIfNoVocalists()
        {
            if (!VocalTrack.gameObject.activeSelf) return;
            if (_players == null) return;

            foreach (var p in _players)
            {
                if (p is VocalsPlayer && p != null && p.gameObject.activeSelf)
                {
                    return;
                }
            }

            YargLogger.LogInfo("GameManager: last vocalist left, hiding vocal highway and falling back to lyric bar");
            VocalTrack.gameObject.SetActive(false);

            // Only re-enable the lyric bar under the same conditions LyricBar's own
            // OnChartLoaded would have used to keep itself active: the chart must
            // have lyrics, we're not in practice mode (lyric bar is hidden during
            // practice by design), and the user hasn't disabled lyric display.
            // Otherwise leave both off — better an empty area than an empty bar.
            if (_lyricBar != null
                && Chart?.Lyrics?.Phrases.Count > 0
                && !IsPractice
                && SettingsManager.Settings.LyricDisplay.Value != LyricDisplayMode.Disabled)
            {
                _lyricBar.gameObject.SetActive(true);
                _lyricBar.SetSongTime(_songRunner.SongTime);
            }
        }

        private void Update()
        {
            // Pause/unpause
            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                if (_draggableHud.EditMode)
                {
                    SetEditHUD(false);
                }

                if ((!IsPractice || PracticeManager.HasSelectedSection) &&
                    !DialogManager.Instance.IsDialogShowing &&
                    !PlayerHasFailed)
                {
                    SetPaused(!_pauseMenu.IsOpen);
                }
            }

            // Toggle debug text
            if (Keyboard.current.ctrlKey.isPressed && Keyboard.current.tabKey.wasPressedThisFrame)
            {
                ToggleDebugEnabled();
            }

            // Skip the rest if paused
            if (_songRunner.Paused)
            {
                return;
            }

            // Update handlers
            _songRunner.Update();
            BeatEventHandler.Update(_songRunner.SongTime, _songRunner.VisualTime);
            CrowdEventHandler.Update(_songRunner.SongTime);

            // Update players
            int totalScore = 0;
            foreach (var player in _players)
            {
                player.GameplayUpdate();

                totalScore += player.Score;
                totalScore += player.BandBonusScore;
            }

            if (GlobalVariables.VerboseReplays)
            {
                _frameTimes.Add(_songRunner.InputTime);
            }

            BandScore = totalScore;
            EngineManager.UpdateStars();

            // End song if needed (required for the [end] event)
            if (_songRunner.SongTime >= SongLength)
            {
                if (EndSong())
                {
                    return;
                }
            }
        }


        public void SetSongTime(double time, double delayTime = SONG_START_DELAY)
        {
            _songRunner.SetSongTime(time, delayTime);

            BeatEventHandler.Reset();
            BackgroundManager.SetTime(_songRunner.SongTime + Song.SongOffsetSeconds);
            VenueCameraManager?.ResetTime(time);
            VenueCharacterManager?.ResetTime(time);
            if (_lyricBar.gameObject.activeSelf)
            {
                _lyricBar.SetSongTime(time);
            }
        }

        public void SetSongSpeed(float speed)
        {
            _songRunner.SetSongSpeed(speed);

            BackgroundManager.SetSpeed(_songRunner.SongSpeed);
        }

        public int GetMixerFFTData(float[] buffer, int fftSize, bool complex)
        {
            return _mixer.GetFFTData(buffer, fftSize, complex);
        }

        public int GetMixerSampleData(float[] buffer)
        {
            return _mixer.GetSampleData(buffer);
        }

        public void AdjustSongSpeed(float deltaSpeed)
        {
            _songRunner.AdjustSongSpeed(deltaSpeed);

            // Only scale the player speed in practice
            if (IsPractice && _songRunner.SongSpeed >= 1)
            {
                // Scale only if the speed is greater than 1
                var speed = _songRunner.SongSpeed >= 1 ? _songRunner.SongSpeed : 1;
                foreach (var player in _players)
                {
                    player.BaseEngine.SetSpeed(speed);
                }
            }

            BackgroundManager.SetSpeed(_songRunner.SongSpeed);
        }

        public void Pause(bool showMenu = true)
        {
            if (!IsOnline)
            {
                _songRunner.Pause();
            }
            PauseCore(showMenu);
        }

        private void PauseCore(bool showMenu)
        {
            if (showMenu)
            {
                if (!GlobalVariables.State.PlayingWithReplay && ReplayInfo != null)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.ReplayPause);
                }
                else if (PlayerHasFailed)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.FailPause);
                }
                else if (IsPractice)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.PracticePause);
                }
                else if (GlobalVariables.State.PlayingAShow)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.SetlistPause);
                }
                else if (IsOnline)
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.OnlinePause);
                }
                else
                {
                    _pauseMenu.PushMenu(PauseMenuManager.Menu.QuickPlayPause);
                }
            }

            // Pause the background/venue. Skipped entirely during online play.
            if (!IsOnline)
            {
                Time.timeScale = 0f;
                BackgroundManager.SetPaused(true);
                GameStateFetcher.SetPaused(true);
            }

            // This uses the raw input update time because it keeps running during the pause
            // allowing us to accurately calculate the length of the pause later
            if (!Rewinding && !IsReplay && showMenu)
            {
                // Save state about the pause
                _pauseTime = InputManager.InputUpdateTime;
                var pauseInfo = new PauseInfo
                {
                    PauseTime = SongTime,
                    PauseLength = 0
                };
                PauseInfo.Add(pauseInfo);

                // Calculate the rewind limit now so it can't be overwritten if the user pauses again before completion
                var rewindTime = Math.Max(SongTime - PAUSE_REWIND_LENGTH, _rewindLimit);
                _rewindLimit = rewindTime;
            }

            _autoCalibrateVideoOnPause = SettingsManager.Settings.AutoCalibrateVideo.Value;

            // Pause any audio samples that are currently playing
            GlobalAudioHandler.PauseAllSfx();

            // Allow sleeping
            Screen.sleepTimeout = _originalSleepTimeout;
        }

        public bool PlayerHasFailed { get; set; } = false;

        public async void Resume(double? rewindDuration = null)
        {
            // Online mode: nothing was actually paused. Just dismiss the
            // menu and return.
            if (IsOnline)
            {
                _pauseMenu.PopAllMenus();
                return;
            }

            // We don't rewind in practice mode or in replay, so we can skip all the BS
            if (IsPractice || IsReplay)
            {
                _pauseMenu.PopAllMenus();
                _songRunner.Resume();
                ResumeCore();
                return;
            }

            if (_resumeInProgress)
            {
                return;
            }

            _resumeInProgress = true;
            Rewinding = true;

            // If AutoCalibrateVideo changed while paused, fade the mixer accordingly
            bool autoCalibrateVideoEnabled = SettingsManager.Settings.AutoCalibrateVideo.Value;
            bool didChangeWhilePaused = autoCalibrateVideoEnabled != _autoCalibrateVideoOnPause;
            if (didChangeWhilePaused)
            {
                if (autoCalibrateVideoEnabled)
                {
                    _preFadeOutVolume = _mixer.GetVolume();
                    _mixer.FadeOut(SONG_START_DELAY);
                }
                else
                {
                    _mixer.FadeIn(_preFadeOutVolume, SONG_START_DELAY);
                }
            }

            // try block is here so we can ensure that _resumeInProgress always gets reset
            try
            {
                _pauseMenu.PopAllMenus();
                Time.timeScale = 1f;

                // Update the last PauseInfo with the pause length
                var currentPause = PauseInfo[^1];
                currentPause.PauseLength = InputManager.InputUpdateTime - _pauseTime;
                PauseInfo[^1] = currentPause;

                // Don't allow rewinding past the rewind limit, unless a duration was explicitly passed to the resume function
                var rewindSeconds = Math.Max(0, rewindDuration ?? SongTime - _rewindLimit);
                if (rewindSeconds == PAUSE_REWIND_LENGTH)
                {
                    GlobalAudioHandler.PlaySoundEffect(SfxSample.Rewind);
                }

                var canceled = await RewindAndResume(rewindSeconds);

                if (canceled)
                {
                    return;
                }

                ResumeCore();
            }
            finally
            {
                _resumeInProgress = false;
            }
        }

        public void UpdateCalibration()
        {
            _songRunner.UpdateCalibration();
        }

        public void ResumeCore()
        {
            if (_draggableHud.EditMode)
            {
                SetEditHUD(false);
            }

            if (!Rewinding)
            {
                _pauseMenu.PopAllMenus();
            }

            if (_songRunner.SongTime >= SongLength + SONG_END_DELAY)
            {
                return;
            }

            // Unpause the background/venue
            Time.timeScale = 1f;
            BackgroundManager.SetPaused(false);
            GameStateFetcher.SetPaused(false);

            // Unpause any audio samples that are currently playing
            GlobalAudioHandler.ResumeAllSfx();

            // Disallow sleeping
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            _isReplaySaved = false;

            Rewinding = false;

            foreach (var player in _players)
            {
                player.SendInputsOnResume();
            }

        }

        public void SetPaused(bool paused)
        {
            // Does not delegate out to _songRunner.SetPaused since we need extra logic
            if (paused)
            {
                Pause();
            }
            else
            {
                Resume();
            }
        }

        public void OverridePause()
        {
            _songRunner.OverridePause();
            PauseCore(showMenu: false);
        }

        public bool OverrideResume()
        {
            bool resumed = _songRunner.OverrideResume();
            if (resumed)
            {
                ResumeCore();
            }

            return resumed;
        }

        public double GetRelativeInputTime(double timeFromInputSystem)
            => _songRunner.GetRelativeInputTime(timeFromInputSystem);

        private bool EndSong()
        {
            // Dispose the crowd handler
            CrowdEventHandler.Dispose();

            if (IsPractice)
            {
                PracticeManager.ResetPractice();
                return false;
            }

            if (_songRunner.SongTime < SongLength + SONG_END_DELAY)
            {
                return false;
            }

            if (!GlobalVariables.State.PlayingWithReplay && ReplayInfo != null)
            {
                Pause(false);
                return true;
            }

            // Notify the relay that this peer's song is over. The server holds GameEnd until
            // every peer reports (or the straggler timer fires), so other clients keep
            // receiving any remaining inputs from us in flight. SendGameComplete also
            // broadcasts this peer's final authoritative engine-state snapshot so
            // receivers can snap their mirror engines before rendering results.
            if (IsOnline && !_gameCompleteSent)
            {
                _gameCompleteSent = true;
                _gameCompleteRealtime = Time.realtimeSinceStartupAsDouble;
                OnlineSession?.SendGameComplete();
            }

            // Wait for every remote peer's FINAL authoritative snapshot
            // (snapshotSongTime >= SongLength) to be applied before
            // transitioning to the results screen. Without this gate the
            // remote players' stat cards would render before the terminal
            // mirror state has been snapped, showing stale mid-song values.
            //
            // Bounded by FINAL_SNAPSHOT_TIMEOUT_SECONDS so a disconnected
            // straggler doesn't pin us at the end-of-song screen forever —
            // when the timeout fires we proceed with whatever stats the
            // last snapshot left on each mirror engine.
            if (IsOnline && OnlineSession != null
                && !OnlineSession.AllRemoteFinalSnapshotsReceived(SongLength))
            {
                double elapsed = Time.realtimeSinceStartupAsDouble - _gameCompleteRealtime;
                if (elapsed < FINAL_SNAPSHOT_TIMEOUT_SECONDS)
                {
                    return false;
                }
                YargLogger.LogFormatWarning(
                    "GameManager: final-snapshot gate timed out after {0:0.0}s — proceeding to results.",
                    elapsed);
            }
#nullable enable
            ReplayInfo? replayInfo = null;
#nullable disable
            try
            {
                _isReplaySaved = false;
                replayInfo = SaveReplay(_songRunner.InputTime, ScoreContainer.ScoreReplayDirectory);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to save replay!");
            }

            // Pass the score info to the stats screen
            GlobalVariables.State.ScoreScreenStats = new ScoreScreenStats
            {
                PlayerScores = _players.Select(player => new PlayerScoreCard
                {
                    IsHighScore = player.Score > player.LastHighScore,
                    Player = player.Player,
                    Stats = player.BaseStats,
                    AverageMultiplier = player.BaseEngine.BaseNoteScore == 0 ?
                        0 :
                        // PendingScore should be 0 at this point, so no reason to add it
                        (float) player.BaseStats.CommittedScore / player.BaseEngine.BaseNoteScore,
                }).ToArray(),
                BandScore = BandScore,
                BandStars = (int) BandStars,
                ReplayInfo = replayInfo,
            };

            RecordScores(replayInfo);

            // Go to the score screen
            GlobalVariables.Instance.LoadScene(SceneIndex.Score);
            return true;
        }

        private void RecordScores(ReplayInfo replayInfo)
        {
            if (!ScoreContainer.IsBandScoreValid(SongSpeed))
            {
                return;
            }

            // Get all of the individual player score entries
            var playerEntries = new List<PlayerScoreRecord>();
            var starScoreCutoffsList = new List<int[]>();
            foreach (var player in _players)
            {
                var profile = player.Player.Profile;

                // Remote players' scores belong to other users. Never write them to our
                // local score store.
                if (player.Player.IsRemote)
                {
                    continue;
                }

                // Skip bots and anyone that's obviously cheating.
                if (!ScoreContainer.IsSoloScoreValid(SongSpeed, player.Player))
                {
                    continue;
                }

                playerEntries.Add(new PlayerScoreRecord
                {
                    PlayerId = profile.Id,

                    Instrument = profile.CurrentInstrument,
                    Difficulty = profile.CurrentDifficulty,

                    EnginePresetId = profile.EnginePreset,

                    Score = player.Score,
                    Stars = StarAmountHelper.GetStarsFromInt((int) player.Stars),

                    NotesHit = player.BaseStats.NotesHit,
                    NotesMissed = player.BaseStats.NotesMissed,
                    IsFc = player.IsFc,
                    IsReplay = player.Player.IsReplay,

                    Percent = player.BaseStats.Percent
                });

                starScoreCutoffsList.Add(player.BaseEngine.StarScoreThresholds);
            }

            var validScoreCount = _players.Count(p => ScoreContainer.IsSoloScoreValid(SongSpeed, p.Player));
            if (validScoreCount == 0)
            {
                return;
            }

            int humanBandScore = 0;
            float humanBandStars = 0;
            int humanCount = playerEntries.Count;
            if (HasBots && SaveScoresWithBots)
            {
                // Simulate the replay with only human players to calculate the correct score.
                // This will remove band multiplier and Star Power contribution from bots
                if (replayInfo == null || ReplayData == null)
                {
                    return;
                }
                var results = ReplayAnalyzer.AnalyzeReplay(Chart, replayInfo, ReplayData);
                foreach (var result in results)
                {
                    humanBandScore += result.ResultStats.TotalScore + result.ResultStats.BandBonusScore;
                }
                var humanStarScoreCutoffs = EngineManager.GetStarScoreCutoffs(starScoreCutoffsList);
                // Determine where in the cutoffs humanBandScore is
                // Iterating backwards is slightly faster assuming people are good at the game
                for (int i = humanStarScoreCutoffs.Length - 1; i >= 0; i--)
                {
                    if (humanBandScore >= humanStarScoreCutoffs[i])
                    {
                        // This gives humanBandStars as an int, which is not exactly correct but should make no difference
                        // since it is converted into StarAmount by int anyway
                        humanBandStars = i + 1;
                        YargLogger.LogFormatDebug("Star count: {0}", humanBandStars);
                        break;
                    }
                }
            }
            else
            {
                // No bots, use live scores directly
                foreach (var player in _players)
                {
                    humanBandScore += player.Score + player.BaseStats.BandBonusScore;
                }
                humanBandStars = EngineManager.Stars;
            }

            var bandStars = humanCount > 0
                ? StarAmountHelper.GetStarsFromInt(Mathf.FloorToInt(humanBandStars))
                : StarAmount.None;

            ScoreContainer.RecordScore(new GameRecord
            {
                Date = DateTime.Now,

                SongChecksum = Song.Hash.HashBytes,
                SongName = Song.Name,
                SongArtist = Song.Artist,
                SongCharter = Song.Charter,

                ReplayFileName = replayInfo?.ReplayName,
                ReplayChecksum = replayInfo?.ReplayChecksum.HashBytes,

                BandScore = humanBandScore,
                BandStars = bandStars,

                SongSpeed = SongSpeed,
                PlayedWithReplay = GlobalVariables.State.PlayingWithReplay,
                HasBots = HasBots,
            }, playerEntries);
        }

        // Latches so the session-ended toast / scene transition fires exactly once
        // even if both GameEnded and Disconnected race in.
        private bool _onlineSessionEndedHandled;

        /// <summary>Invoked on the Unity main thread when the underlying online
        /// session dies mid-load or mid-song (server-broadcast GameEnd, transport
        /// disconnect, or last remote leaving). Bails out of the gameplay scene
        /// gracefully — without this, the song plays through as a fake-offline
        /// run with frozen remote highways.</summary>
        private void OnOnlineSessionEnded(bool hadLocalProgress)
        {
            if (_onlineSessionEndedHandled) return;
            _onlineSessionEndedHandled = true;

            YargLogger.LogFormatWarning(
                "GameManager: online session ended externally (hadLocalProgress={0}); bailing out of gameplay.",
                hadLocalProgress);

            // User-visible feedback. Toast survives the scene transition because
            // ToastManager lives on the persistent scene.
            try
            {
                ToastManager.ToastWarning(
                    YARG.Localization.Localize.Key("Menu.Online.Toast.SessionEndedDuringGame"));
            }
            catch (Exception ex) { YargLogger.LogException(ex); }

            // Clear the IsOnline flag and any pending engine work so the bail
            // path doesn't try to send a final snapshot through a dead session.
            // ForceQuitSong handles the LeaveCurrentGame call (harmless if the
            // session is already torn down server-side — the LobbyHub call
            // tolerates a no-op).
            ForceQuitSong();
        }

        public void ForceQuitSong()
        {
            // Mid-song bail-out: tear down the UDP game session so the relay
            // tells remaining peers we left (they hide our highway) and
            // flip our lobby-side IsBackInLobby flag so the host's Start
            // gate ungates promptly. Skipping this would leave a phantom
            // track on every remaining peer's screen for the rest of the
            // song. Must run BEFORE we reset PersistentState — IsOnline
            // is read from the gameplay-scope flag, not GlobalVariables.
            if (IsOnline)
            {
                LobbyHubSession.Current?.LeaveCurrentGame();
            }
            GlobalVariables.State = PersistentState.Default;
            BailOutToMenu();
        }

        /// <summary>
        /// Common Gameplay -> Menu exit path. When we're in an online lobby
        /// session, sets MenuManager's override so the next MenuScene Start
        /// lands the user on LobbyView. Otherwise behaves identically to a
        /// plain <c>LoadScene(Menu)</c>.
        /// </summary>
        internal static void BailOutToMenu()
        {
            if (IsOnline && LobbyHubSession.Current?.CurrentLobby != null)
            {
                MenuManager.SetOverrideOpenMenu(MenuManager.Menu.LobbyView);
            }
            GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
        }

        public void SetVenueCameraManager(CameraManager cameraManager)
        {
            VenueCameraManager = cameraManager;
        }

        public void SetVenueCharacterManager(CharacterManager characterManager)
        {
            VenueCharacterManager = characterManager;
        }

        public void SetEditHUD(bool on)
        {
            if (on)
            {
                _pauseMenu.gameObject.SetActive(false);
                _draggableHud.SetEditHUD(true);
            }
            else
            {
                _draggableHud.SetEditHUD(false);
                _pauseMenu.gameObject.SetActive(true);
            }
        }

#nullable enable
        public ReplayInfo? SaveReplay(double length, string directory)
#nullable disable
        {
            if (_isReplaySaved)
            {
                return null;
            }

            var frames = new List<ReplayFrame>(_players.Count);
            var replayStats = new List<ReplayStats>(_players.Count);
            var colorProfiles = new Dictionary<Guid, ColorProfile>();
            var cameraPresets = new Dictionary<Guid, CameraPreset>();

            int bandScore = 0;
            float bandStars = EngineManager.Stars;
            for (int i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                if (player.Player.Profile.IsBot || player.Player.IsRemote)
                {
                    continue;
                }

                var (frame, stats) = player.ConstructReplayData();
                frames.Add(frame);
                replayStats.Add(stats);
                bandScore += player.Score;

                if (!player.Player.ColorProfile.DefaultPreset)
                {
                    colorProfiles.TryAdd(player.Player.ColorProfile.Id, player.Player.ColorProfile);
                }

                if (!player.Player.CameraPreset.DefaultPreset)
                {
                    cameraPresets.TryAdd(player.Player.CameraPreset.Id, player.Player.CameraPreset);
                }
            }

            if (frames.Count == 0)
            {
                return null;
            }

            var stars = StarAmountHelper.GetStarsFromInt(Mathf.FloorToInt(bandStars));
            ReplayData = new ReplayData(colorProfiles, cameraPresets, frames.ToArray(), _frameTimes.ToArray());

            (bool success, var replayInfo) = ReplayIO.TrySerialize(directory, Song, SongSpeed, length, bandScore, stars, PauseInfo.ToArray(), replayStats.ToArray(), ReplayData);
            if (!success)
            {
                return null;
            }

            ReplayContainer.AddEntry(replayInfo);
            _isReplaySaved = true;
            return replayInfo;
        }

        private void OnNavigationEvent(NavigationContext context)
        {
            switch (context.Action)
            {
                // Pause
                case MenuAction.Start:
                    if (_draggableHud.EditMode)
                    {
                        SetEditHUD(false);
                    }

                    if ((!IsPractice || PracticeManager.HasSelectedSection)
                        && !DialogManager.Instance.IsDialogShowing
                        && !PlayerHasFailed)
                    {
                        SetPaused(!_songRunner.Paused);
                    }
                    break;
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && !Paused && SettingsManager.Settings.PauseOnFocusLoss.Value)
            {
                SetPaused(true);
            }
        }

        public void ResetBandCombo()
        {
            switch (_bandComboType)
            {
                case BandComboType.Strict:
                    BandCombo = 0;
                break;
                case BandComboType.Lenient:
                    BandCombo = Players.Sum(e => e.Combo * e.BaseStats.BandComboUnits);
                break;
            }
        }

        public void AddBandCombo(int amount)
        {
            BandCombo += amount;
        }

        private async void OnSongFailed()
        {
            if (SettingsManager.Settings.NoFail.Value != NoFailMode.Off ||
                IsPractice || IsOnline)
            {
                return;
            }

            if (!PlayerHasFailed)
            {
                PlayerHasFailed = true;

                if (_players.Count > 1)
                {
                    // For some reason you seem to need this many frames to pass before pause for every highway to lower?
                    await UniTask.DelayFrame(_players.Count - 1);
                }

                // Pause gameplay immediately, but don't show the menu until the highways have lowered
                _songRunner.Pause();
                _mixer.FadeOut(SONG_END_DELAY);
                await UniTask.Delay(TimeSpan.FromSeconds(SONG_END_DELAY));
                GlobalAudioHandler.PlayVoxSample(VoxSample.FailSound);
                Pause();
            }
        }

        public void UnfailSong()
        {
            YargLogger.LogFormatDebug("Unfailing song at SongTime {0}", SongTime);
            PlayerHasFailed = false;
            _mixer.FadeIn(DEFAULT_VOLUME, SONG_START_DELAY);
            InvalidateScores("Menu.Toast.ResumeAfterFailInvalidate");
            // This is an arbitrary value, just want to give players enough time to adjust
            Resume(SONG_START_DELAY + 1);
        }
        // If we go from no fail to fail, we need to reinitialize the happiness state so we avoid
        // the possibility of an instant fail. Yes, this is cheeseable since toggling no fail resets happiness.
        private void OnNoFailModeChanged(NoFailMode mode)
        {
            // If we're going from no fail to fail and happiness would result in an insta-fail, reset happiness,
            // but also inhibit score saving to avoid cheesing
            if (mode == NoFailMode.Off && EngineManager.Happiness <= 0f)
            {
                InvalidateScores("Menu.Toast.NoFailScore");
                EngineManager.InitializeHappiness();
            }
            _failMeter.SetActive(mode != NoFailMode.NoMeter);
        }

        internal void InvalidateScores(string toastKey)
        {
            bool invalidated = false;

            foreach (var player in _players)
            {
                if (player.Player.IsScoreValid)
                {
                    invalidated = true;
                }

                player.Player.IsScoreValid = false;
            }

            if (invalidated && !string.IsNullOrEmpty(toastKey))
            {
                ToastManager.ToastWarning(Localize.Key(toastKey));
            }
        }

        private void CheckForRewindInvalidation()
        {
            if (PauseInfo.Count == 0)
            {
                return;
            }

            // If there is more than MAXIMUM_REWIND_TIME seconds of rewind in MAXIMUM_REWIND_WINDOW of song time, invalidate scores
            var start = 0;

            for (var end = 0; end < PauseInfo.Count; end++)
            {
                var endTime = PauseInfo[end].PauseTime;

                while (PauseInfo[start].PauseTime < endTime - MAXIMUM_REWIND_WINDOW)
                {
                    start++;
                }

                var pauses = end - start + 1;

                if (pauses * PAUSE_REWIND_LENGTH > MAXIMUM_REWIND_TIME)
                {
                    InvalidateScores("Menu.Toast.TooManyPauses");
                    return;
                }
            }
        }

        private async UniTask<bool> RewindAndResume(double seconds)
        {
            YargLogger.LogFormatDebug("Rewinding {0} seconds at VisualTime {1}", seconds, VisualTime);

            // Rewind players
            foreach (var player in _players)
            {
                player.Rewind(VisualTime - seconds);
            }

            double? targetTime = null;
            if (PauseInfo.Count > 0)
            {
                targetTime = PauseInfo[^1].PauseTime;
            }

            var canceled = await _songRunner.RewindAndResume(seconds, targetTime);

            if (canceled)
            {
                return true;
            }

            foreach (var player in _players)
            {
                player.PostRewind(VisualTime - seconds);
            }

            CheckForRewindInvalidation();

            return false;
        }

        public void StartCoda(CodaSection _)
        {
            if (_breBoxActive)
            {
                return;
            }

            _breBoxActive = true;
            _breBox.StartCoda(EngineManager);
        }

        public void EndCoda(CodaSection coda)
        {
            _breBox.EndCoda(EngineManager.TotalCodaBonus, () => { _breBoxActive = false; });
        }

        public void ResetCoda()
        {
            _breBox.ForceReset();
            _breBoxActive = false;
        }
    }
}
