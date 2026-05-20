using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PlasticBand.Haptics;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;
using YARG.Helpers.UI;
using YARG.Input;
using YARG.Online;
using YARG.Playback;
using YARG.Player;
using YARG.Settings;

namespace YARG.Gameplay.Player
{
    public abstract class BasePlayer : GameplayBehaviour
    {
        public int HighwayIndex { get; private set; }

        public YargPlayer Player { get; private set; }

        public float NoteSpeed
        {
            get
            {
                float noteSpeed = Player.Profile.NoteSpeed * _noteSpeedDifficultyScale;

                // If we're in a replay, don't change the note speed (it should be like a video
                // slowing down/speeding up). The actual song speed should be taken into account though,
                // which is saved in the engine parameter override.
                if (Player.IsReplay)
                {
                    return noteSpeed / (float) Player.EngineParameterOverride.SongSpeed;
                }

                if (GameManager.IsPractice && GameManager.SongSpeed < 1)
                {
                    return noteSpeed;
                }

                return noteSpeed / GameManager.SongSpeed;
            }
        }

        /// <summary>
        /// The player's input calibration, in seconds.
        /// </summary>
        /// <remarks>
        /// Be aware that this value is negated!
        /// Positive calibration settings will result in a negative number here.
        /// </remarks>
        public double InputCalibration => -Player.Profile.InputCalibrationSeconds;

        public abstract BaseEngine BaseEngine { get; }

        public BaseStats BaseStats => BaseEngine.BaseStats;
        public BaseEngineParameters BaseParameters => BaseEngine.BaseParameters;

        /// <summary>
        /// <p> Star thresholds, from 1 to 5 stars, then gold stars. </p>
        /// <p> These values represent multiples of the score if you were to FC, hold all sustains fully, hit no dynamics, and use no star power. </p>
        /// <p> Multiplying these by the max multiplier of the instrument will also roughly give you the average multiplier needed for that star. </p>
        /// </summary>
        protected abstract float[] StarMultiplierThresholds { get; set; }

        /// <summary>
        /// Multiples of the maximum points it is possible to get from a solo, which is then added to each star point threshold (1 to 5, then gold stars).
        /// <seealso cref="StarMultiplierThresholds"/>
        /// </summary>
        protected readonly float[] SoloBonusStarMultiplierThresholds = {
            0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f
        };

        public abstract bool ShouldUpdateInputsOnResume { get; }

        public HitWindowSettings HitWindow { get; protected set; }

        public float Stars => BaseStats.Stars;

        public int Score => BaseStats.TotalScore;
        public int BandBonusScore => BaseStats.BandBonusScore;
        public int Combo => BaseStats.Combo;
        public int NotesHit => BaseStats.NotesHit;

        public int TotalNotes { get; protected set; }

        public bool IsFc { get; protected set; }

        public int? LastHighScore { get; private set; }

        public IReadOnlyList<GameInput> ReplayInputs => _replayInputs.AsReadOnly();

        private Dictionary<int, GameInput> LastInputs { get; } = new();
        private Dictionary<int, GameInput> InputsToSendOnResume { get; } = new();

        protected SyncTrack SyncTrack { get; private set; }

        protected bool IsInitialized { get; private set; }

        protected List<ISantrollerHaptics> SantrollerHaptics { get; private set; } = new();

        protected BaseInputViewer InputViewer { get; private set; }

        protected int  LastCombo;
        protected bool IsStemMuted;

        private List<GameInput> _replayInputs;

        private int _replayInputIndex;

        private float _noteSpeedDifficultyScale;

        protected EngineManager.EngineContainer EngineContainer;

        protected override void GameplayAwake()
        {
            _replayInputs = new List<GameInput>();

            // TODO: Couldn't there be more than one input viewer?
            //  We were using FindObjectOfType<BaseInputViewer> before anyway, so we're no worse off in that respect
            InputViewer = FindFirstObjectByType<BaseInputViewer>();

            IsFc = true;
        }

        private void Update()
        {
            //Ensure hud elements get repositioned on screen size change
            if (ScreenSizeDetector.HasScreenSizeChanged)
            {
                UpdateVisuals(GameManager.VisualTime);
            }
        }

        protected void Start()
        {
            if (Player.Bindings is not null)
            {
                SantrollerHaptics = Player.Bindings.GetDevicesByType<ISantrollerHaptics>();
            }

            if (!Player.IsReplay && !Player.IsRemote)
            {
                SubscribeToInputEvents();
            }
        }

        protected void Initialize(int index, YargPlayer player, SongChart chart, int? lastHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            HighwayIndex = index;
            Player = player;

            SyncTrack = chart.SyncTrack;

            LastHighScore = lastHighScore;

            _noteSpeedDifficultyScale = Player.Profile.CurrentDifficulty.NoteSpeedScale();

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                _replayInputs = new List<GameInput>(GameManager.ReplayData.Frames[player.ReplayIndex].Inputs);
                YargLogger.LogFormatDebug("Initialized replay inputs with {0} inputs", _replayInputs.Count);
            }

            if (InputViewer != null)
            {
                InputViewer.SetColors(player.ColorProfile);
                InputViewer.ResetButtons();
            }

            IsInitialized = true;
        }

        public virtual void GameplayUpdate()
        {
            if (!GameManager.Started || GameManager.Paused)
            {
                return;
            }

            if (!GameManager.Rewinding)
            {
                UpdateInputs(GameManager.InputTime);
            }

            UpdateVisuals(GameManager.VisualTime);
        }

        protected abstract void UpdateVisuals(double visualTime);
        protected abstract void ResetVisuals();
        public abstract void Rewind(double visualTime);
        public abstract void PostRewind(double visualTime);

        public virtual void ResetPracticeSection()
        {
            LastCombo = 0;

            IsFc = true;

            ResetVisuals();
        }

        public abstract void SetPracticeSection(uint start, uint end);

        // TODO Make this more generic
        public abstract void SetStemMuteState(bool muted);

        public virtual void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(SongStem.Song, active);
        }

        public virtual void SetReplayTime(double time)
        {
            IsFc = true;

            _replayInputIndex = BaseEngine.ProcessUpToTime(time, ReplayInputs);

            SetStemMuteState(false);

            ResetVisuals();
            UpdateVisuals(time);
        }

        protected override void GameplayDestroy()
        {
            // Mirror the gating in Start() — UnsubscribeFromInputEvents touches Player.Bindings
            // which is null for both replay and remote players.
            if (!Player.IsReplay && !Player.IsRemote)
            {
                UnsubscribeFromInputEvents();
            }

            FinishDestruction();
        }

        protected virtual void FinishDestruction()
        {
        }

        protected virtual void UpdateInputs(double time)
        {
            // Apply input offset
            // Video offset is already accounted for
            time += InputCalibration;

            if (Player.IsRemote)
            {
                // Drain the network pipe up to the *delayed* engine clock. Ticking the
                // remote engine at (time - REMOTE_DELAY_SECONDS) means inputs arrive before
                // the engine reaches their timestamp, so QueueInput's backward-time clamp
                // (BaseEngine.cs:339) effectively never fires. Late inputs that exceed the
                // delay budget would be silently mis-scored by that clamp; size the delay
                // accordingly.
                //
                // The pipe carries GameInput-shaped 16-byte records written directly by
                // GameClientSession's LiteNetLib poll thread (SPSC, no lock). Layout is
                // GameInput's explicit-layout (Time@0 / Action@8 / Integer@12, Pack=1),
                // so MemoryMarshal.Read<GameInput> reconstructs each input as-is.
                var reader = GameManager.OnlineSession?.GetInputReader(Player);
                double remoteEngineTime = time - REMOTE_ENGINE_DELAY_SECONDS;
                int recordSize = Unsafe.SizeOf<GameInput>();
                if (reader != null && reader.TryRead(out var result))
                {
                    var buf = result.Buffer;
                    var consumed = buf.Start;
                    Span<byte> tmp = stackalloc byte[16]; // reused across cross-segment iterations
                    while (buf.Length >= recordSize)
                    {
                        GameInput input;
                        var firstSpan = buf.First.Span;
                        if (firstSpan.Length >= recordSize)
                        {
                            // Fast path: the next 16 bytes are contiguous — read in place.
                            input = MemoryMarshal.Read<GameInput>(firstSpan);
                        }
                        else
                        {
                            // Slow path: the record straddles a segment boundary. Walk the
                            // sliced sub-sequence and gather into the stack buffer.
                            var chunk = buf.Slice(0, recordSize);
                            int written = 0;
                            foreach (var segment in chunk)
                            {
                                segment.Span.CopyTo(tmp.Slice(written));
                                written += segment.Length;
                            }
                            input = MemoryMarshal.Read<GameInput>(tmp);
                        }
                        if (input.Time > remoteEngineTime) break;
                        BaseEngine.QueueInput(ref input);
                        OnInputQueued(input);
                        buf = buf.Slice(recordSize);
                        consumed = buf.Start;
                    }
                    // Mark unread tail as examined so TryRead returns false next frame
                    // unless new bytes have arrived — avoids spinning on a partial record.
                    reader.AdvanceTo(consumed, buf.End);
                }

                BaseEngine.Update(remoteEngineTime);
                return;
            }

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                while (_replayInputIndex < ReplayInputs.Count)
                {
                    var input = ReplayInputs[_replayInputIndex];

                    // Current input does not meet the time requirement
                    if (time < input.Time)
                    {
                        break;
                    }

                    BaseEngine.QueueInput(ref input);
                    OnInputQueued(input);

                    _replayInputIndex++;
                }
            }

            BaseEngine.Update(time);
        }

        // Wall-clock delay (in song-time seconds) applied to remote engines. Pulled forward
        // from the local song clock so network-delivered inputs slot in before the engine
        // reaches their timestamp. 120 ms covers typical home-internet jitter to a regional
        // relay; bump in v2 if testing surfaces frequent backward-clamp warnings.
        private const double REMOTE_ENGINE_DELAY_SECONDS = 0.120;

        private void SubscribeToInputEvents()
        {
            Player.Bindings.SubscribeToGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded += OnDeviceAdded;
            Player.Bindings.DeviceRemoved += OnDeviceRemoved;
        }

        private void UnsubscribeFromInputEvents()
        {
            Player.Bindings.UnsubscribeFromGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded -= OnDeviceAdded;
            Player.Bindings.DeviceRemoved -= OnDeviceRemoved;
        }

        private void OnDeviceAdded(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Add(haptics);
            }
        }

        private void OnDeviceRemoved(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Remove(haptics);
            }

            if (!GameManager.Paused && SettingsManager.Settings.PauseOnDeviceDisconnect.Value)
            {
                GameManager.SetPaused(true);
            }
        }

        public void SendInputsOnResume()
        {
            foreach (var originalInput in InputsToSendOnResume.Values)
            {
                var input = new GameInput(InputManager.CurrentInputTime, originalInput.Action, originalInput.Integer);
                OnGameInput(ref input);
            }

            InputsToSendOnResume.Clear();
        }

        protected void OnGameInput(ref GameInput input)
        {
            // Ignore completely if the song hasn't started yet or player failed
            if (!GameManager.Started || GameManager.PlayerHasFailed)
                return;

            // Ignore while paused
            if (GameManager.Paused || GameManager.Rewinding)
            {
                if (!ShouldUpdateInputsOnResume)
                {
                    return;
                }

                if (LastInputs.TryGetValue(input.Action, out var lastInput))
                {
                    if (lastInput.Button != input.Button)
                    {
                        InputsToSendOnResume[input.Action] = input;
                    }
                    else
                    {
                        InputsToSendOnResume.Remove(input.Action);
                    }
                }

                return;
            }

            LastInputs[input.Action] = input;

            double adjustedTime = GameManager.GetRelativeInputTime(input.Time);
            // Apply input offset
            adjustedTime += InputCalibration;
            input = new(adjustedTime, input.Action, input.Integer);

            // Allow the input to be explicitly ignored before processing it
            if (InterceptInput(ref input)) return;

            BaseEngine.QueueInput(ref input);
            OnInputQueued(input);
            _replayInputs.Add(input);

            // Forward to the network *after* the local engine has consumed the input, so
            // network send latency cannot stall local play. Input.Time is now in song-time
            // (post GetRelativeInputTime + InputCalibration), which is what the wire expects.
            // Fully qualified to disambiguate from the inherited GameManager instance property.
            if (YARG.Gameplay.GameManager.IsOnline)
            {
                GameManager.OnlineSession?.EnqueueLocalInput(Player, input);
            }
        }

        protected virtual void OnStarPowerPhraseHit()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerAward);
            }
        }

        protected virtual void OnStarPowerPhraseMissed()
        {

        }

        protected virtual void OnStarPowerStatus(bool active)
        {
            var deploySample = SfxSample.StarPowerDeploy;
            if (SettingsManager.Settings.UseCrowdFx.Value == CrowdFxMode.Enabled)
            {
                deploySample = SfxSample.StarPowerDeployCrowd;
            }

            if (!GameManager.Paused)
            {
                GlobalAudioHandler.PlaySoundEffect(active
                    ? deploySample
                    : SfxSample.StarPowerRelease);

                SetStarPowerFX(active);
            }

            GameManager.ChangeStarPowerStatus(active);

            foreach (var haptics in SantrollerHaptics)
            {
                haptics.SetStarPowerActive(active);
            }
        }

        protected abstract bool InterceptInput(ref GameInput input);

        protected virtual void OnInputQueued(GameInput input)
        {
            if (InputViewer != null)
            {
                InputViewer.OnInput(input);
            }
        }

        protected void OnComboIncrement(int amount)
        {
            GameManager.AddBandCombo(amount);
        }

        protected void OnComboReset()
        {
            GameManager.ResetBandCombo();
        }

        public abstract (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData();
    }
}
