using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Gameplay.Player;
using YARG.Localization;
using YARG.Menu;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Menu.Settings;
using YARG.Online;
using YARG.Playback;
using YARG.Player;
using YARG.Scores;
using YARG.Settings;
using YARG.Song;

namespace YARG.Gameplay
{
    public partial class GameManager
    {
        private enum LoadFailureState
        {
            None,
            Rescan,
            Error
        }

        [Header("Instrument Prefabs")]
        [SerializeField]
        private GameObject _fiveFretGuitarPrefab;
        [SerializeField]
        private GameObject _sixFretGuitarPrefab;
        [SerializeField]
        private GameObject _fourLaneDrumsPrefab;
        [SerializeField]
        private GameObject _fiveLaneDrumsPrefab;
        [SerializeField]
        private GameObject _proKeysPrefab;
        [SerializeField]
        private GameObject _fiveLaneKeysPrefab;
        [SerializeField]
        private GameObject _proGuitarPrefab;

        private LoadFailureState _loadState;
        private string _loadFailureMessage;

        // All access to chart data must be done through this event,
        // since things are loaded asynchronously
        // Players are initialized by hand and don't go through this event
        private event Action<SongChart> _chartLoaded;

        public event Action<SongChart> ChartLoaded
        {
            add
            {
                _chartLoaded += value;

                // Invoke now if already loaded, this event is only fired once
                var chart = Chart;
                if (chart != null) value?.Invoke(chart);
            }
            remove => _chartLoaded -= value;
        }

        private event Action _songLoaded;

        public event Action SongLoaded
        {
            add
            {
                _songLoaded += value;

                // Invoke now if already loaded, this event is only fired once
                if (_mixer != null)
                {
                    value?.Invoke();
                }
            }
            remove => _songLoaded -= value;
        }

        private event Action _songReady;

        public event Action SongReady
        {
            add
            {
                _songReady += value;

                // Invoke now if already ready, this event is only fired once
                if (IsSongReady) value?.Invoke();
            }
            remove => _songReady -= value;
        }

        private event Action _songStarted;

        public event Action SongStarted
        {
            add
            {
                _songStarted += value;

                // Invoke now if already loaded, this event is only fired once
                if (IsSongStarted) value?.Invoke();
            }
            remove => _songStarted -= value;
        }

        private async void Start()
        {
            var global = GlobalVariables.Instance;

            // Disable until everything's loaded
            enabled = false;

            YargLogger.LogFormatInfo("Loading song {0} - {1}", Song.Name, Song.Artist);

            // ──────── Phase 1+2: load and synchronize, all under the loading screen ────────
            // Firing SongReady inside the using block preserves today's timing — subscriber
            // setup work (e.g. QuickSettings caching its FailMeter reference) runs while the
            // loading screen is still visible. The online clock-sync and cue-wait run under
            // the loading screen too; the cue's wall-clock-alignment wait does NOT — instead
            // it gets folded into the visible pre-roll so the user sees the highway scrolling
            // during it (set via the preRoll value below).
            double preRoll = SONG_START_DELAY;
            using (var context = new LoadingContext())
            {
                if (ReplayInfo != null)
                {
                    if (!SongContainer.SongsByHash.TryGetValue(GlobalVariables.State.CurrentReplay.SongChecksum, out var songs))
                    {
                        ToastManager.ToastWarning("Song not present in library");
                        BailOutToMenu();
                        return;
                    }
                    Song = songs[0];

                    context.SetLoadingText("Loading replay...");
                    if (!LoadReplay())
                    {
                        ToastManager.ToastError("Failed to load replay!");
                        BailOutToMenu();
                        return;
                    }

                    if (!GlobalVariables.State.PlayingWithReplay)
                    {
                        _replayController.gameObject.SetActive(true);
                    }
                    else
                    {
                        _replayController.gameObject.SetActive(false);
                        var players = new List<YargPlayer>();
                        players.AddRange(PlayerContainer.Players);
                        for (int i = 0; i < YargPlayers.Count; i++)
                        {
                             players.Add(YargPlayers[i]);
                        }

                        YargPlayers = players.ToArray();
                    }

                    var replayIndex = 0;
                    foreach (var player in YargPlayers)
                    {
                        if (player.IsReplay)
                        {
                            player.ReplayIndex = replayIndex;
                            replayIndex++;
                        }
                    }
                }

                context.Queue(UniTask.RunOnThreadPool(LoadChart), "Loading chart...");
                context.Queue(UniTask.RunOnThreadPool(LoadAudio), "Loading audio...");
                await context.Wait();

                if (_loadState == LoadFailureState.Rescan)
                {
                    ToastManager.ToastWarning("Chart requires a rescan!", () =>
                    {
                        MenuManager.Instance.DisableCurrentMenu();
                        SettingsMenu.Instance.gameObject.SetActive(true);
                        SettingsMenu.Instance.SelectTabByName("SongManager");
                    });

                    BailOutToMenu();
                    return;
                }

                if (_loadState == LoadFailureState.Error)
                {
                    YargLogger.LogError(_loadFailureMessage);
                    ToastManager.ToastError(_loadFailureMessage);

                    BailOutToMenu();
                    return;
                }

                FinalizeChart();

                // Initialize song runner up front so any code that reads GameManager.SongTime /
                // VisualTime during init (e.g. BasePlayer.Update on a screen-size change) sees a
                // valid runner. SONG_START_DELAY gives both solo and online a uniform 2s pre-roll
                // before audio kicks in — without it, notes that fall near song time 0 appear to
                // "skip ahead" because the highway has nothing scrolling on screen first. For
                // online, every peer's BeginPlayback fires at the same wall-clock instant
                // (SongOriginUtcMs), and the pre-roll then runs concurrently on each client.
                _songRunner = new SongRunner(
                    _mixer,
                    startTime: 0,
                    startDelay: SONG_START_DELAY,
                    GlobalVariables.State.SongSpeed,
                    Song.SongOffsetSeconds);

                // Spawn players
                CreatePlayers();
                YargLogger.LogFormatDebug("Calculating star cutoffs for {0} players", _players.Count);
                EngineManager.StarScoreThresholds = EngineManager.GetStarScoreCutoffs(_players.ConvertAll(p => p.BaseEngine.StarScoreThresholds));
                YargLogger.LogFormatDebug("Star score thresholds: {0}", string.Join(", ", EngineManager.StarScoreThresholds));


                // Set up the crowd stem so it can be restored after muting (if it exists)
                if (_stemStates.TryGetValue(SongStem.Crowd, out var state))
                {
                    state.Total = 1;
                    state.Audible = 1;
                }

                if (_loadState == LoadFailureState.Error)
                {
                    ToastManager.ToastError(_loadFailureMessage);

                    BailOutToMenu();
                    return;
                }

                // Listen for menu inputs
                Navigator.Instance.NavigationEvent += OnNavigationEvent;

                // Debug info
                InitializeDebug();
#if UNITY_EDITOR
                SetDebugEnabled(true);
#endif

                // Initialize/destroy practice mode
                if (IsPractice)
                {
                    PracticeManager.DisplayPracticeMenu();
                }
                else
                {
                    Destroy(PracticeManager);
                }

                _failMeter.Initialize(EngineManager, this);

                if (SettingsManager.Settings.NoFail.Value == NoFailMode.NoMeter || IsPractice)
                {
                    _failMeter.SetActive(false);
                }

                // This is not an else because we still want to subscribe in case the user disables no fail during the song
                // We check in the callback to determine whether we should actually run the fail routine
                if (ReplayInfo == null || GlobalVariables.State.PlayingWithReplay)
                {
                    EngineManager.OnSongFailed += OnSongFailed;

                    EngineManager.InitializeHappiness();

                    SettingsManager.Settings.NoFail.OnChange += OnNoFailModeChanged;
                    SettingsManager.Settings.AutoCalibrateAudio.Value = false;
                    SettingsManager.Settings.AutoCalibrateVideo.Value = false;
                }

                EngineManager.OnCodaStart += StartCoda;
                EngineManager.OnCodaEnd += EndCoda;

                // Log constant values
                YargLogger.LogFormatDebug("Audio calibration: {0}, video calibration: {1}, song offset: {2}",
                    _songRunner.AudioCalibration, _songRunner.VideoCalibration, _songRunner.SongOffset);

                // All per-client init is done. Audio system is fully prepared (mixer +
                // stems loaded, SongRunner constructed). Announce ready so subscribers
                // can do their last setup pass under the loading screen — must NOT do
                // per-frame gameplay work; that stays gated by SongStarted below.
                IsSongReady = true;
                _songReady?.Invoke();

                // Online sync + cue wait runs WHILE the loading screen is still up; the
                // wall-clock-alignment wait is NOT held here — instead it gets absorbed
                // into the visible pre-roll below so the user sees the highway scrolling
                // during it. Solo falls through immediately.
                preRoll = await NegotiateStartTimingAsync(context);
            }   // ← LoadingContext disposed: loading screen hidden, menu input re-enabled

            // ──────── Phase 3: deterministic playback kick ────────
            // The pre-roll runs inside BeginPlayback's sync thread (SyncVisualTime ramps
            // from -preRoll to 0 before _mixer.Play()), so the user sees the highway +
            // player names with notes scrolling in for the entire wait. For online, that
            // pre-roll includes the wall-clock-alignment delay — audio still hits exactly
            // SongOriginUtcMs + SONG_START_DELAY on every peer.
            _songRunner.BeginPlayback(preRoll);

            // Loaded, enable updates
            enabled = true;
            IsSongStarted = true;
            _songStarted?.Invoke();
        }

        /// <summary>
        /// Online start handshake: sync the local→server clock offset, announce PeerReady,
        /// wait for the server's GameStartCue. Returns the desired pre-roll length —
        /// solo gets <see cref="SONG_START_DELAY"/>; online gets <c>secondsUntilOrigin +
        /// SONG_START_DELAY</c> so the wall-clock-alignment wait happens during a visible
        /// track-scrolling animation rather than under the loading screen.
        /// </summary>
        private async UniTask<double> NegotiateStartTimingAsync(LoadingContext context)
        {
            if (!IsOnline) return SONG_START_DELAY;

            context.SetLoadingText(Localize.Key("Menu.Online.Handshake.SyncingPeers"));

            // Sync the local→server clock offset over UDP before announcing PeerReady.
            // The cue carries a wall-clock SongOriginUtcMs picked by the server; without
            // this offset, alignment across peers is bounded by whatever the OS-level
            // clock skew happens to be (often 100ms+, sometimes seconds). The server
            // gates the cue on PeerReady, so spending ~400ms here just shifts pre-roll
            // into our budget — no protocol change needed.
            if (ServerClockSync.Current != null)
            {
                bool ok = await ServerClockSync.Current.RunSyncBurstAsync(
                    ct: this.GetCancellationTokenOnDestroy());
                if (!ok)
                {
                    YargLogger.LogWarning(
                        "Server clock sync failed; song start may be misaligned across peers.");
                }
            }
            else
            {
                YargLogger.LogWarning(
                    "ServerClockSync.Current is null at online song start; falling back to raw " +
                    "local wall clock.");
            }

            context.SetLoadingText(Localize.Key("Menu.Online.Handshake.WaitingForPlayers"));
            OnlineSession?.SendPeerReady();
            await OnlineSession.WaitForStartCueAsync();

            if (OnlineSession.TryGetSongStartOffsetSeconds(out double secondsUntilOrigin))
            {
                if (secondsUntilOrigin > 0)
                {
                    // Roll the wall-clock-alignment wait into the visible pre-roll. Every
                    // peer's BeginPlayback fires roughly when its cue arrives, but each
                    // uses a per-peer pre-roll length = (SongOriginUtcMs - now) + standard
                    // pre-roll. Audio still lands at the same wall-clock instant on every
                    // peer (within local clock skew), just preceded by a longer visible
                    // ramp on peers whose cue arrived earlier.
                    YargLogger.LogFormatInfo(
                        "Online start: absorbing {0:0.000}s wall-clock wait into visible pre-roll",
                        secondsUntilOrigin);
                    return secondsUntilOrigin + SONG_START_DELAY;
                }

                YargLogger.LogFormatWarning(
                    "GameStartCue arrived after SongOriginUtcMs by {0:0.000}s; starting with minimum pre-roll",
                    -secondsUntilOrigin);
            }
            else
            {
                YargLogger.LogWarning(
                    "Online start cue missing SongOriginUtcMs; falling back to local-clock start");
            }

            return SONG_START_DELAY;
        }

        private bool LoadReplay()
        {
            var readOptions = new ReplayReadOptions { KeepFrameTimes = GlobalVariables.VerboseReplays };
            var (result, data) = ReplayIO.TryLoadData(ReplayInfo, readOptions);
            if (result != ReplayReadResult.Valid)
            {
                YargLogger.LogFormatError("Failed to load replay! Result: {0}", result);
                return false;
            }

            // Create YargPlayers from the replay frames
            var players = new YargPlayer[data.Frames.Length];
            for (int i = 0; i < data.Frames.Length; ++i)
            {
                players[i] = new YargPlayer(data.Frames[i], data);
            }

            ReplayData = data;
            YargPlayers = players;
            return true;
        }

        private void LoadChart()
        {
            try
            {
                Chart = Song.LoadChart();
                if (Chart != null)
                {
                    GenerateVenueTrack();
                    GenerateLipsyncTrack();
                }
                else
                {
                    _loadState = LoadFailureState.Rescan;
                }
            }
            catch (Exception ex)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load chart!";
                YargLogger.LogException(ex, "Failed to load chart!");
            }
        }

        private void GenerateVenueTrack()
        {
            // If we have no venue events, attempt to load from milo
            if (Chart.VenueTrack.IsEmpty)
            {
                    SongChart.LoadVenueFromMilo(Chart, Song);

                    YargLogger.LogFormatDebug("Loaded {0} lighting events from milo", Chart.VenueTrack.Lighting.Count);
            }

            if (File.Exists(VenueAutoGenerationPreset.DefaultPath))
            {
                var preset = new VenueAutoGenerationPreset(VenueAutoGenerationPreset.DefaultPath);
                if (!preset.ChartHasFog(Chart)) // This is separate because we may want to add fog even if venue is authored
                {
                    Chart = preset.GenerateFogEvents(Chart);
                }

                if (Chart.VenueTrack.Lighting.Count == 0)
                {
                    Chart = preset.GenerateLightingEvents(Chart);
                }
            }
        }

        private void GenerateLipsyncTrack()
        {
            SongChart.LoadLipsyncFromMilo(Chart, Song);

            YargLogger.LogFormatDebug("Loaded {0} lipsync events from milo", Chart.LipsyncEvents.Count);
        }

        private void FinalizeChart()
        {
            double audioLength = _mixer.Length;
            double chartLength = Chart.GetEndTime();
            double endTime = Chart.GetEndEvent()?.Time ?? -1;

            // - Chart < Audio < [end] -> Audio
            // - Chart < [end] < Audio -> [end]
            // - [end] < Chart < Audio -> Audio
            // - Audio < Chart         -> Chart
            if (audioLength <= chartLength)
            {
                SongLength = chartLength;
            }
            else if (endTime <= chartLength || audioLength <= endTime)
            {
                SongLength = audioLength;
            }
            else
            {
                SongLength = endTime;
            }

            // Get the first and last note times for the chart
            FirstNoteTime = Chart.GetFirstNoteStartTime();
            LastNoteTime = Chart.GetLastNoteEndTime();

            // Make sure enough beatlines have been generated to cover the song end delay
            Chart.SyncTrack.GenerateBeatlines(SongLength + SONG_END_DELAY, true);

            BeatEventHandler = new BeatEventHandler(Chart.SyncTrack);
            CrowdEventHandler = new CrowdEventHandler(Chart, this);

            _chartLoaded?.Invoke(Chart);

            _songLoaded?.Invoke();
        }

        private void CreatePlayers()
        {
            try
            {
                _players = new List<BasePlayer>();

                bool vocalTrackInitialized = false;

                int index = -1;
                int highwayIndex = -1;
                int vocalIndex = -1;
                foreach (var player in YargPlayers)
                {
                    player.IsScoreValid = true;

                    if (!player.IsReplay && !player.IsRemote)
                    {
                        // Reset microphone (resets channel buffers)
                        // We probably wanna do this no matter what, so put it up here
                        player.Bindings.Microphone?.Reset();
                    }

                    // Skip if the player is sitting out — but ONLY for local players. A
                    // remote can land in CreatePlayers with SittingOut=true if the game
                    // server's RemotePeerLeftPacket arrived during the cue-handshake
                    // window (OnRemotePeerLeftEvent flips SittingOut on receipt, and
                    // RemotePlayerLeft fires before _players exists so the
                    // HideRemotePlayerHighway subscriber no-ops). Skipping here would
                    // leave the remote sim out of _players entirely, so its engine
                    // never ticks and the wire-event pipeline never delivers anything
                    // visible for the whole song. Create them anyway and hide
                    // post-init below so the sim keeps draining + the score panel
                    // still represents them.
                    if (player.SittingOut && !player.IsRemote)
                    {
                        YargLogger.LogFormatInfo(
                            "CreatePlayers: skipping local player {0} (SittingOut)",
                            player.Profile?.Name ?? "<null>");
                        continue;
                    }
                    if (player.SittingOut && player.IsRemote)
                    {
                        YargLogger.LogFormatWarning(
                            "CreatePlayers: remote player {0} (peerId={1}) is SittingOut already — " +
                            "creating anyway and will hide highway. RemotePeerLeftPacket likely arrived " +
                            "during game-start before _players existed.",
                            player.Profile?.Name ?? "<null>", player.RemotePeerId);
                    }
                    index++;

                    if (!player.IsReplay)
                    {
                        // Don't do this if it's a replay, because the replay
                        // would've already set its own presets at this point
                        player.RefreshPresets();
                    }

                    var lastHighScore = ScoreContainer.GetHighScore(Song.Hash, player.Profile.Id, player.Profile.CurrentInstrument, false)?.Score;
                    YargLogger.LogFormatInfo("Current high score for player {0} on {1}: {2}",
                        player.Profile.Name, player.Profile.CurrentInstrument, lastHighScore ?? 0);

                    if (player.Profile.GameMode != GameMode.Vocals)
                    {
                        highwayIndex++;
                        var prefab = player.Profile.GameMode switch
                        {
                            GameMode.FiveFretGuitar => _fiveFretGuitarPrefab,
                            GameMode.SixFretGuitar  => _sixFretGuitarPrefab,
                            GameMode.FourLaneDrums  => _fourLaneDrumsPrefab,
                            GameMode.FiveLaneDrums  => _fiveLaneDrumsPrefab,
                            GameMode.EliteDrums     => Song.HasInstrument(Instrument.FiveLaneDrums) ? _fiveLaneDrumsPrefab : _fourLaneDrumsPrefab,
                            GameMode.ProKeys        => player.Profile.CurrentInstrument is Instrument.ProKeys ? _proKeysPrefab : _fiveLaneKeysPrefab,
                            GameMode.ProGuitar      => _proGuitarPrefab,
                            _                       => null
                        };

                        // Skip if there's no prefab for the game mode
                        if (prefab == null) continue;

                        var playerObject = Instantiate(prefab,
                            new Vector3(highwayIndex * TRACK_SPACING_X, 100f, 0f), prefab.transform.rotation);

                        // Setup player
                        var trackPlayer = playerObject.GetComponent<TrackPlayer>();
                        var trackView = _trackViewManager.CreateTrackView();
                        trackPlayer.Initialize(highwayIndex, player, Chart, trackView, _mixer, lastHighScore);

                        _players.Add(trackPlayer);
                        _trackViewManager.AddTrackPlayer(trackPlayer);
                    }
                    else
                    {
                        // Initialize the vocal track if it hasn't been already, and hide lyric bar
                        if (!vocalTrackInitialized)
                        {
                            highwayIndex++;
                            VocalTrack.gameObject.SetActive(true);
                            // Position the vocal track at its highway slot so the shader's WorldPosToIndex maps it correctly
                            VocalTrack.transform.position = new Vector3(highwayIndex * TRACK_SPACING_X, 100, 0);
                            _trackViewManager.CreateVocalTrackView(highwayIndex);

                            // Since all players have to select the same vocals
                            // type (solo/harmony) this works no problem.
                            var chart = player.Profile.CurrentInstrument == Instrument.Vocals
                                ? Chart.Vocals
                                : Chart.Harmony;
                            VocalTrack.Initialize(chart, player, Song.VocalScrollSpeedScalingFactor);

                            _lyricBar.gameObject.SetActive(false);
                            vocalTrackInitialized = true;
                        }

                        // Create the player on the vocal track

                        var vocalsPlayer = VocalTrack.CreatePlayer();
                        vocalIndex++;
                        var playerHud = _trackViewManager.CreateVocalsPlayerHUD();

                        var percussionTrack = VocalTrack.CreatePercussionTrack();
                        percussionTrack.TrackSpeed = VocalTrack.TrackSpeed;
                        vocalsPlayer.Initialize(index, vocalIndex, player, Chart, playerHud, percussionTrack, lastHighScore, VocalTrack.TrackSpeed);

                        _players.Add(vocalsPlayer);
                    }

                    // Add (or increase total of) the stem state
                    var stem = player.Profile.CurrentInstrument.ToSongStem();
                    if (stem == SongStem.Bass && !_stemStates.ContainsKey(SongStem.Bass))
                    {
                        stem = SongStem.Rhythm;
                    }

                    if (stem != _backgroundStem && _stemStates.TryGetValue(stem, out var state))
                    {
                        ++state.Total;
                        ++state.Audible;
                    }
                    else if (_stemStates.TryGetValue(_backgroundStem, out state))
                    {
                        // Ensures the stem will still play at a minimum of 50%, even if all players mute
                        state.Total += 2;
                        state.Audible += 2;
                    }
                }

                // Post-init pass: any remote player that was already SittingOut at the
                // moment we entered CreatePlayers (because RemotePeerLeftPacket arrived
                // during the cue handshake before _players existed) needs its highway
                // hidden now that _players is populated — HideRemotePlayerHighway was a
                // no-op when it fired. The sim is still in _remoteSimulators so wire
                // events for that peer keep draining; we just don't render them.
                foreach (var basePlayer in _players)
                {
                    if (basePlayer?.Player == null) continue;
                    if (!basePlayer.Player.IsRemote) continue;
                    if (!basePlayer.Player.SittingOut) continue;
                    YargLogger.LogFormatInfo(
                        "CreatePlayers: hiding pre-flagged-SittingOut remote {0} (peerId={1})",
                        basePlayer.Player.Profile?.Name ?? "<null>", basePlayer.Player.RemotePeerId);
                    HideRemotePlayerHighway(basePlayer.Player.RemotePeerId);
                }
            }
            catch (Exception ex)
            {
                _loadState = LoadFailureState.Error;
                _loadFailureMessage = "Failed to load song!";
                YargLogger.LogException(ex, "Failed to load song!");
            }
        }
    }
}
