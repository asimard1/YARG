using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Engine.Vocals;
using YARG.Core.Engine.Vocals.Engines;
using YARG.Core.Input;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Helpers;
using YARG.Input;
using YARG.Player;
using YARG.Settings;

namespace YARG.Gameplay.Player
{
    public class VocalsPlayer : BasePlayer
    {
        public VocalsEngineParameters EngineParams { get; private set; }
        public VocalsEngine           Engine       { get; private set; }

        public override BaseEngine BaseEngine => Engine;

        [SerializeField]
        private GameObject _needleVisualContainer;
        [SerializeField]
        private MeshRenderer _needleRenderer;
        [SerializeField]
        private Transform _needleTransform;
        [SerializeField]
        private ParticleGroup _hittingParticleGroup;

        public override bool ShouldUpdateInputsOnResume => false;

        protected override float[] StarMultiplierThresholds { get; set; } =
        {
            0.05f, 0.11f, 0.19f, 0.46f, 0.77f, 1.06f
        };

        private InstrumentDifficulty<VocalNote> NoteTrack { get; set; }
        private InstrumentDifficulty<VocalNote> OriginalNoteTrack { get; set; }

        private MicInputContext _inputContext;

        private VocalNote _lastTargetNote;
        private double?   _lastHitTime;
        private double?   _lastSingTime;
        private double    _previousStarPowerPercent;
        private bool      _hotStartChecked;
        private bool      _newHighScoreShown;

        private VocalsPlayerHUD _hud;
        private VocalPercussionTrack _percussionTrack;
        private bool _shouldHideNeedle;

        private int _phraseIndex = -1;

        private const int NEEDLES_COUNT = 7;

        private SongChart _chart;

        // Monotonic cursor for FindRemoteTargetNoteAt (O(1) amortized per frame).
        private int _remoteTargetPhraseCursor;

        // EWMA of (sungPitch - chartTarget) -- captures the singer's habitual pitch offset
        // for predicting needle position at the start of each new phrase.
        private float _remotePitchOffsetEwma;
        private int   _remotePitchOffsetSamples;
        private const float RemotePitchOffsetEwmaAlpha = 0.20f;
        private const float RemotePitchOffsetEwmaMaxSemitones = 2.0f;

        // Previous-frame needle visibility; rising edge triggers a position snap.
        private bool _remoteNeedleWasVisible;

        // Tracks whether singing samples have arrived this phrase (predict vs follow toggle).
        private VocalNote _remotePhraseTrackedNote;
        private bool      _remoteSamplesArrivedThisPhrase;

        /// <summary>Deactivates this vocalist's HUD card, which lives on a shared canvas.</summary>
        public void HideHud()
        {
            if (_hud != null)
            {
                _hud.gameObject.SetActive(false);
            }
        }

        public void Initialize(int index, int vocalIndex, YargPlayer player, SongChart chart,
            VocalsPlayerHUD hud, VocalPercussionTrack percussionTrack, int? lastHighScore, float trackSpeed)
        {
            if (IsInitialized)
            {
                return;
            }

            base.Initialize(index, player, chart, lastHighScore);

            // Save the chart
            _chart = chart;

            // Needle materials have names starting from 1.
            var needleIndex = (vocalIndex % NEEDLES_COUNT) + 1;
            var materialPath = $"VocalNeedle/{needleIndex}";
            _needleRenderer.material = Addressables.LoadAssetAsync<Material>(materialPath).WaitForCompletion();

            // Get the notes from the specific harmony or solo part

            var multiTrack = chart.GetVocalsTrack(Player.Profile.CurrentInstrument);

            var track = multiTrack.Parts[Player.Profile.HarmonyIndex];
            player.Profile.ApplyVocalModifiers(track);

            OriginalNoteTrack = track.CloneAsInstrumentDifficulty();
            NoteTrack = OriginalNoteTrack;

            _phraseIndex = -1;
            _previousStarPowerPercent = 0.0;

            // Update speed of particles
            var particles = _hittingParticleGroup.GetComponentsInChildren<ParticleSystem>();
            foreach (var system in particles)
            {
                // This interface is weird lol, `.main` is readonly but
                // doesn't need to be re-assigned, changes are forwarded automatically
                var main = system.main;

                var startSpeed = main.startSpeed;
                startSpeed.constant *= trackSpeed;
                main.startSpeed = startSpeed;
                main.startColor = VocalTrack.Colors[Player.Profile.HarmonyIndex];
            }

            // Initialize player specific vocal visuals

            hud.Initialize(player.EnginePreset);
            _hud = hud;

            percussionTrack.Initialize(NoteTrack.Notes);
            _percussionTrack = percussionTrack;

            _hud.ShowPlayerName(player, needleIndex);

            // Remote players have no local mic; their pitch arrives over the network.
            if (!Player.IsReplay && !Player.IsRemote && player.Bindings.Microphone != null)
            {
                _inputContext = new MicInputContext(player.Bindings.Microphone, GameManager);
                _inputContext.Start();
            }

            Engine = CreateEngine();

            Engine.OnComboIncrement += OnComboIncrement;
            Engine.OnComboReset += OnComboReset;

            if (vocalIndex == 0)
            {
                if (Player.Profile.CurrentInstrument == Instrument.Vocals)
                {
                    Engine.BuildCountdownsFromSelectedPart();
                }
                else
                {
                    Engine.BuildCountdownsFromAllParts(multiTrack.Parts);
                }

                Engine.OnCountdownChange += (countdownLength, endTime) =>
                {
                    GameManager.VocalTrack.UpdateCountdown(countdownLength, endTime);
                };
            }

            if (GameManager.IsPractice)
            {
                Engine.SetSpeed(GameManager.SongSpeed >= 1 ? GameManager.SongSpeed : 1);
            }
            else
            {
                Engine.SetSpeed(GameManager.SongSpeed);
            }

            // Online sync wiring (mirrors TrackPlayer.FinishInitialization).
            var director = YARG.Online.OnlineSessionDirector.Current;
            if (director != null)
            {
                bool isLocalEligible = !player.IsReplay && !player.Profile.IsBot && !player.IsRemote;
                if (isLocalEligible)
                {
                    director.AttachLocalEngineForSync(Engine);
                }
                else if (player.IsRemote)
                {
                    var sim = new YARG.Core.Engine.Prediction.RemotePlayerSimulator<VocalNote>(
                        Engine, NoteTrack.Notes);
                    director.RegisterRemoteSimulator(player.RemotePeerId, sim);
                }
            }
        }

        protected override void FinishDestruction()
        {
            _inputContext?.Stop();
        }

        protected VocalsEngine CreateEngine()
        {
            if (!Player.IsReplay)
            {
                var singToActivateStarPower = SettingsManager.Settings.VoiceActivatedVocalStarPower.Value;

                // Create the engine params from the engine preset
                EngineParams = Player.EnginePreset.Vocals.Create(StarMultiplierThresholds, SoloBonusStarMultiplierThresholds,
                    Player.Profile.CurrentDifficulty, MicDevice.UPDATES_PER_SECOND, singToActivateStarPower);
            }
            else
            {
                // Otherwise, get from the replay
                EngineParams = (VocalsEngineParameters) Player.EngineParameterOverride;
            }

            // The hit window can just be taken from the params
            HitWindow = EngineParams.HitWindow;

            var engine = new YargVocalsEngine(NoteTrack, SyncTrack, EngineParams, Player.Profile.IsBot);
            EngineContainer = GameManager.EngineManager.Register(engine, NoteTrack.Instrument, Player.Profile.HarmonyIndex, _chart, Player.RockMeterPreset);

            engine.OnStarPowerPhraseHit += _ => OnStarPowerPhraseHit();
            engine.OnStarPowerStatus += OnStarPowerStatus;

            engine.OnTargetNoteChanged += (note) =>
            {
                _lastTargetNote = note;
            };

            engine.OnPhraseHit += (percent, fullPoints, isLastPhrase) =>
            {
                if (!fullPoints)
                {
                    IsFc = false;
                }

                LastCombo = Combo;

                ShowTextNotifications(isLastPhrase);

                // Order is important here. ShowVocalPhraseResult() will skip showing AWESOME! if other, more important notifications are already showing.
                _hud.ShowPhraseHit(percent, Combo);
            };

            engine.OnNoteHit += (_, note) =>
            {
                if (note.IsPercussion)
                {
                    _percussionTrack.HitPercussionNote(note);
                }
            };

            engine.OnNoteMissed += (_, _) =>
            {
                if (LastCombo >= 2 && !Player.IsRemote)
                {
                    GlobalAudioHandler.PlaySoundEffect(SfxSample.NoteMiss);
                }

                LastCombo = Combo;
            };

            engine.OnSing += (singing) =>
            {
                _lastSingTime = singing
                    ? GameManager.InputTime
                    : null;
            };

            engine.OnHit += (hitting) =>
            {
                _lastHitTime = hitting
                    ? GameManager.InputTime
                    : null;
            };

            return engine;
        }

        protected override void ResetVisuals()
        {
            _lastTargetNote = null;
        }

        public override void ResetPracticeSection()
        {
            Engine.Reset(true);

            if (NoteTrack.Notes.Count > 0)
            {
                NoteTrack.Notes[0].OverridePreviousNote();
                NoteTrack.Notes[^1].OverrideNextNote();
            }

            _phraseIndex = -1;
            _percussionTrack.Initialize(NoteTrack.Notes);

            base.ResetPracticeSection();
        }

        public override void Rewind(double visualTime)
        {
            _hittingParticleGroup.Stop();
        }

        public override void PostRewind(double visualTime)
        {
            ResetVisuals();
            UpdateVisuals(visualTime);
        }

        private const double VocalPitchSendInterval = 0.05; // ~20 Hz; receivers interpolate
        // Grace period before emitting singing->silent to avoid flicker from mic batching gaps.
        private const double VocalPitchSilenceGraceSeconds = 0.15;
        private double _lastVocalPitchSendTime = double.NegativeInfinity;
        private bool   _lastVocalPitchIsSingingSent;
        private double _vocalPitchSilencePendingAt = double.NaN; // NaN = no pending transition

        protected override void UpdateInputs(double time)
        {
            if (!Player.IsReplay && !Player.IsRemote && _inputContext != null)
            {
                foreach (var input in _inputContext.GetInputsFromMic())
                {
                    var i = input;
                    OnGameInput(ref i);
                }
            }

            base.UpdateInputs(time);

            // Publish pitch to remote peers (debounced, with silence grace period).
            if (GameManager.IsOnline && !Player.IsReplay && !Player.IsRemote)
            {
                bool isSinging = IsInThreshold(time, _lastSingTime);

                if (isSinging)
                {
                    _vocalPitchSilencePendingAt = double.NaN;
                }
                else if (_lastVocalPitchIsSingingSent && double.IsNaN(_vocalPitchSilencePendingAt))
                {
                    _vocalPitchSilencePendingAt = time;
                }

                bool stillInGrace = !isSinging
                    && _lastVocalPitchIsSingingSent
                    && !double.IsNaN(_vocalPitchSilencePendingAt)
                    && (time - _vocalPitchSilencePendingAt) < VocalPitchSilenceGraceSeconds;
                bool effectiveIsSinging = isSinging || stillInGrace;

                bool stateChanged = effectiveIsSinging != _lastVocalPitchIsSingingSent;
                bool dueForSample = effectiveIsSinging
                    && (time - _lastVocalPitchSendTime) >= VocalPitchSendInterval;

                if (stateChanged || dueForSample)
                {
                    YARG.Online.OnlineSessionDirector.Current?
                        .SendLocalVocalPitch(time, Engine.PitchSang, effectiveIsSinging);
                    _lastVocalPitchSendTime       = time;
                    _lastVocalPitchIsSingingSent  = effectiveIsSinging;

                    if (!effectiveIsSinging)
                    {
                        _vocalPitchSilencePendingAt = double.NaN;
                    }
                }
            }
        }

        private bool IsInThreshold(double currentTime, double? lastTime)
        {
            if (lastTime is null)
            {
                return false;
            }

            return currentTime - lastTime.Value <= 1f / EngineParams.ApproximateVocalFps + 0.05;
        }

        // Returns the active pitched vocal note at songTime, or null if between phrases.
        private VocalNote FindRemoteTargetNoteAt(double songTime)
        {
            var notes = NoteTrack?.Notes;
            if (notes is null || notes.Count == 0)
            {
                return null;
            }

            // Reset the cursor if the player rewound past the cached phrase.
            if (_remoteTargetPhraseCursor > 0 && songTime < notes[_remoteTargetPhraseCursor].Time)
            {
                _remoteTargetPhraseCursor = 0;
            }

            // Advance past phrases whose end-of-tail is in the past.
            while (_remoteTargetPhraseCursor < notes.Count - 1
                   && songTime > notes[_remoteTargetPhraseCursor].TotalTimeEnd)
            {
                _remoteTargetPhraseCursor++;
            }

            var phrase = notes[_remoteTargetPhraseCursor];
            if (songTime < phrase.Time || songTime > phrase.TotalTimeEnd)
            {
                return null;
            }

            foreach (var child in phrase.ChildNotes)
            {
                if (child.IsPercussion) continue;
                if (songTime >= child.Time && songTime <= child.TotalTimeEnd)
                {
                    return child;
                }
            }

            return null;
        }

        protected override void UpdateVisuals(double visualTime)
        {
            UpdatePercussionPhrase(visualTime);
            UpdateSingNeedle();

            // Get combo meter fill
            float fill = 0f;
            if (Engine.PhraseTicksTotal != null && Engine.PhraseTicksTotal.Value != 0)
            {
                fill = (float) (Engine.PhraseTicksHit / Engine.PhraseTicksTotal.Value);
                fill /= (float) EngineParams.PhraseHitPercent;
            }

            // In multiplayer, don't double the score multiplier in the strikeline element
            // Otherwise, it looks like the band multiplier applies on top of the score multiplier
            var engineStats = Engine.EngineStats;
            int displayMultiplier = GameManager.TotalPlayers > 1 && engineStats.IsStarPowerActive
                ? engineStats.ScoreMultiplier / 2
                : engineStats.ScoreMultiplier;

            // Update HUD
            _hud.UpdateInfo(fill, displayMultiplier,
                (float) Engine.GetStarPowerBarAmount(), Engine.EngineStats.IsStarPowerActive);
        }

        private void ShowTextNotifications(bool isLastPhrase)
        {
            if (SettingsManager.Settings.DisableTextNotifications.Value)
            {
                return;
            }

            var isStarPowerActive = Engine.EngineStats.IsStarPowerActive;
            var currentStarPowerPercent = Engine.GetStarPowerBarAmount();
            if (!isStarPowerActive && _previousStarPowerPercent < 0.5 && currentStarPowerPercent >= 0.5)
            {
                _hud.ShowNotification(TextNotificationType.StarPowerReady);

            }
            _previousStarPowerPercent = Engine.GetStarPowerBarAmount();

            var isMaxMultiplier = Engine.EngineStats.ScoreMultiplier == (isStarPowerActive ? 8 : 4);

            if (!_hotStartChecked && isMaxMultiplier && IsFc)
            {
                _hud.ShowNotification(TextNotificationType.HotStart);
                _hotStartChecked = true;
            }

            if (LastHighScore != null && !_newHighScoreShown && Score > LastHighScore)
            {
                _hud.ShowNotification(TextNotificationType.NewHighScore);
                _newHighScoreShown = true;
            }

            if (!isLastPhrase)
            {
                return;
            }
            if (IsFc)
            {
                _hud.ShowNotification(TextNotificationType.FullCombo);
            }
            else if (isMaxMultiplier)
            {
                _hud.ShowNotification(TextNotificationType.StrongFinish);
            }
        }

        private float GetNeedleRotation(float pitchDist)
        {
            const float NEEDLE_ROT_MAX = 12f;

            // Reduce the provided distance by applying a dead zone. This will prevent oversteer if the player's current pitch is well within the "Perfect" window.
            var deadzoneInSemitones = EngineParams.PitchWindowPerfect / 2;
            var adjustedPitchDist = ApplyPitchDeadZone(pitchDist, deadzoneInSemitones);

            // Determine how off that is compared to the hit window
            float distPercent = Mathf.Clamp(adjustedPitchDist / (EngineParams.PitchWindow - deadzoneInSemitones), -1f, 1f);

            // Use that to get the target rotation
            return distPercent * NEEDLE_ROT_MAX;
        }

        private float ApplyPitchDeadZone(float pitchDist, float deadZoneInSemitones)
        {
            if (pitchDist >= 0.0f)
            {
                return Mathf.Max(0.0f, pitchDist - deadZoneInSemitones);
            }

            return Mathf.Min(0.0f, pitchDist + deadZoneInSemitones);
        }

        private void UpdateSingNeedle()
        {
            const float NEEDLE_POS_LERP = 30f;
            const float NEEDLE_POS_SNAP_MULTIPLIER = 10f;

            const float NEEDLE_ROT_LERP = 25f;

            // LOCAL: pitch from mic input. REMOTE: interpolated from network pitch packets.
            float pitchSang;
            bool  isCurrentlySinging;
            bool  isHittingTarget;
            if (Player.IsRemote)
            {
                var director = YARG.Online.OnlineSessionDirector.Current;
                (float remotePitch, bool remoteSinging) = director != null
                    ? director.GetRemoteVocalPitch(Player.RemotePeerId, GameManager.SongTime)
                    : (0f, false);

                // Remote: scan chart directly since mirror engine's OnTargetNoteChanged won't fire.
                _lastTargetNote = FindRemoteTargetNoteAt(GameManager.SongTime);

                bool hasActiveTarget = _lastTargetNote is not null;
                isCurrentlySinging   = remoteSinging;
                isHittingTarget      = false;
                pitchSang            = remotePitch;

                if (!ReferenceEquals(_remotePhraseTrackedNote, _lastTargetNote))
                {
                    _remotePhraseTrackedNote = _lastTargetNote;
                    _remoteSamplesArrivedThisPhrase = false;
                }

                if (remoteSinging && hasActiveTarget)
                {
                    float targetPitch = _lastTargetNote.PitchAtSongTime(GameManager.SongTime);
                    (float pitchDist, _) = GetPitchDistanceIgnoringOctave(targetPitch, remotePitch);
                    bool onPitch = Mathf.Abs(pitchDist) <= EngineParams.PitchWindow;

                    if (!_lastTargetNote.IsNonPitched)
                    {
                        // Only fold on-pitch samples into EWMA; off-pitch is noise.
                        if (onPitch)
                        {
                            _remotePitchOffsetEwma =
                                _remotePitchOffsetEwma * (1f - RemotePitchOffsetEwmaAlpha)
                                + pitchDist * RemotePitchOffsetEwmaAlpha;
                            if (_remotePitchOffsetEwma > RemotePitchOffsetEwmaMaxSemitones)
                                _remotePitchOffsetEwma = RemotePitchOffsetEwmaMaxSemitones;
                            else if (_remotePitchOffsetEwma < -RemotePitchOffsetEwmaMaxSemitones)
                                _remotePitchOffsetEwma = -RemotePitchOffsetEwmaMaxSemitones;
                            _remotePitchOffsetSamples++;
                        }

                        // Before first sample: predict at chartTarget + EWMA. After: follow live pitch.
                        if (_remoteSamplesArrivedThisPhrase)
                        {
                            pitchSang = remotePitch;
                        }
                        else
                        {
                            pitchSang = targetPitch + _remotePitchOffsetEwma;
                        }
                        isHittingTarget = onPitch;

                        _remoteSamplesArrivedThisPhrase = true;
                    }
                }
            }
            else
            {
                var singTime = GameManager.InputTime;
                pitchSang          = Engine.PitchSang;
                isCurrentlySinging = IsInThreshold(singTime, _lastSingTime);
                isHittingTarget    = _lastTargetNote is not null && IsInThreshold(singTime, _lastHitTime);
            }

            if (!isCurrentlySinging || _shouldHideNeedle)
            {
                // Hide the needle if there's no singing
                if (_needleVisualContainer.activeSelf)
                {
                    _needleVisualContainer.SetActive(false);
                    _hittingParticleGroup.Stop();
                }
                _remoteNeedleWasVisible = false;
            }
            else
            {
                float lerpRate = NEEDLE_POS_LERP;

                // Show needle
                if (!_needleVisualContainer.activeSelf)
                {
                    _needleVisualContainer.SetActive(true);

                    // Lerp X times faster if we've just started showing the needle
                    lerpRate *= NEEDLE_POS_SNAP_MULTIPLIER;
                }

                // Snap needle to predicted position on rising edge to avoid visible lerp from prior phrase.
                if (Player.IsRemote && !_remoteNeedleWasVisible
                    && _lastTargetNote is { IsNonPitched: false })
                {
                    float predictedTarget =
                        _lastTargetNote.PitchAtSongTime(GameManager.SongTime)
                        + _remotePitchOffsetEwma;
                    float predictedZ = GameManager.VocalTrack.GetPosForPitch(predictedTarget);
                    var snapPos = transform.localPosition;
                    snapPos.z = predictedZ;
                    transform.localPosition = snapPos;
                }
                _remoteNeedleWasVisible = true;

                var transformCache = transform;
                float lastNotePitch = _lastTargetNote?.PitchAtSongTime(GameManager.SongTime) ?? -1f;

                if (isHittingTarget)
                {
                    // Show particles if hitting (as long as we aren't rewinding)
                    if (!GameManager.Rewinding)
                    {
                        _hittingParticleGroup.Play();
                    }

                    float pitch;
                    float targetRotation = 0f;

                    if (!_lastTargetNote.IsNonPitched)
                    {
                        // If the player is hitting, just set the needle position to the note
                        pitch = lastNotePitch;

                        // Rotate the needle a little bit depending on how off it is (unless it's non-pitched)
                        // Get how off the player is
                        (float pitchDist, _) = GetPitchDistanceIgnoringOctave(lastNotePitch, pitchSang);
                        targetRotation = GetNeedleRotation(pitchDist);
                    }
                    else
                    {
                        // If the note is non-pitched, just use the singing position
                        pitch = pitchSang + 12f;
                    }

                    // Transform!
                    float z = GameManager.VocalTrack.GetPosForPitch(pitch);
                    var lerp = Mathf.Lerp(transformCache.localPosition.z, z, Time.deltaTime * lerpRate);
                    transformCache.localPosition = new Vector3(0f, 0f, lerp);
                    _needleTransform.rotation = Quaternion.Lerp(_needleTransform.rotation,
                        Quaternion.Euler(0f, targetRotation + 90f, 0f), Time.deltaTime * NEEDLE_ROT_LERP);
                }
                else
                {
                    // Stop particles if not hitting
                    _hittingParticleGroup.Stop();

                    // Since the player is not hitting the note here, we need to offset it correctly.
                    // Get the pitch, and move to the correct octave.
                    float pitch = pitchSang;
                    if (_lastTargetNote is not null && !_lastTargetNote.IsNonPitched)
                    {
                        (_, int octaveShift) = GetPitchDistanceIgnoringOctave(lastNotePitch, pitch);

                        int lastNoteOctave = (int) (lastNotePitch / 12f);

                        // Set the pitch's octave to the target one
                        pitch = pitchSang % 12f;
                        pitch += 12f * (lastNoteOctave + octaveShift);
                    }
                    else
                    {
                        // Hard code a value of one octave up to
                        // make the needle sit more in the middle
                        pitch += 12f;
                    }

                    // Set the position of the needle
                    var z = GameManager.VocalTrack.GetPosForPitch(pitch);
                    var lerp = Mathf.Lerp(transformCache.localPosition.z, z, Time.deltaTime * lerpRate);
                    transformCache.localPosition = new Vector3(0f, 0f, lerp);

                    // Lerp the rotation to none
                    _needleTransform.rotation = Quaternion.Lerp(_needleTransform.rotation,
                        Quaternion.Euler(0f, 90f, 0f), Time.deltaTime * NEEDLE_ROT_LERP);
                }
            }
        }

        private void UpdatePercussionPhrase(double time)
        {
            // Prevent the HUD from hiding too quickly
            if (time < 0)
            {
                return;
            }

            while (ShouldAdvancePhraseIndex(time))
            {
                _phraseIndex++;

                // We've reached the end. No need to continue.
                if (_phraseIndex >= NoteTrack.Notes.Count)
                {
                    SetPercussionMode(false);
                    return;
                }

                var phrase = NoteTrack.Notes[_phraseIndex];
                SetPercussionMode(HasPercussion(phrase));
            }
        }

        private bool ShouldAdvancePhraseIndex(double time)
        {
            // Use phrase end-times for advancement; index -1 handles the first phrase.
            bool beforeFirstPhrase = _phraseIndex == -1;
            if (beforeFirstPhrase)
            {
                // Track has no notes. Bail early.
                if (NoteTrack.Notes.Count <= 0)
                {
                    return false;
                }

                var firstPhrase = NoteTrack.Notes[0];
                var firstPhraseHasStarted = firstPhrase.Time <= time;
                return firstPhraseHasStarted || HasPercussion(firstPhrase);
            }

            bool atTheEndOfTrack = _phraseIndex >= NoteTrack.Notes.Count;
            if (atTheEndOfTrack)
            {
                return false;
            }

            var currentPhrase = NoteTrack.Notes[_phraseIndex];
            return currentPhrase.TimeEnd <= time;
        }

        private void SetPercussionMode(bool show)
        {
            _hud.SetHUDShowing(!show);
            _percussionTrack.ShowPercussionFret(show);
            _shouldHideNeedle = show;
        }

        private static bool HasPercussion(VocalNote phrase)
        {
            foreach (var note in phrase.ChildNotes)
            {
                if (note.IsPercussion)
                {
                    return true;
                }
            }

            return false;
        }

        public override void SetPracticeSection(uint start, uint end)
        {
            var practiceNotes = OriginalNoteTrack.Notes.Where(n => n.Tick >= start && n.Tick < end).ToList();

            NoteTrack = new InstrumentDifficulty<VocalNote>(
                OriginalNoteTrack.Instrument,
                OriginalNoteTrack.Difficulty,
                practiceNotes,
                OriginalNoteTrack.Phrases,
                OriginalNoteTrack.TextEvents);

            _phraseIndex = -1;

            Engine = CreateEngine();
            ResetPracticeSection();
        }

        public override void SetStemMuteState(bool muted)
        {
            // Vocals has no stem muting
        }

        protected override bool InterceptInput(ref GameInput input)
        {
            return false;
        }

        /// <returns>
        /// The first value in the pair (<c>Distance</c>) is the distance between <paramref name="target"/> and '
        /// <paramref name="other"/> ignoring the octave.<br/>
        /// The second value in the pair (<c>OctaveShift</c>) is how much the <paramref name="target"/> octave
        /// had to be shifted in order for the closest distance to be found.
        /// </returns>
        /// <param name="target">The target note (as MIDI pitch).</param>
        /// <param name="other">The other note (as MIDI pitch).</param>
        private static (float Distance, int OctaveShift) GetPitchDistanceIgnoringOctave(float target, float other)
        {
            // Normalize the parameters
            target %= 12f;
            other %= 12f;

            // Start off with the current octave
            float closest = other - target;
            int octaveShift = 0;

            // Upper octave
            float upperDist = (other + 12f) - target;
            if (Mathf.Abs(upperDist) < Mathf.Abs(closest))
            {
                closest = upperDist;
                octaveShift = 1;
            }

            // Lower octave
            float lowerDist = (other - 12f) - target;
            if (Mathf.Abs(lowerDist) < Mathf.Abs(closest))
            {
                closest = lowerDist;
                octaveShift = -1;
            }

            return (closest, octaveShift);
        }

        public override (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData()
        {
            var frame = new ReplayFrame(Player.Profile, EngineParams, Engine.EngineStats, ReplayInputs.ToArray());
            return (frame, Engine.EngineStats.ConstructReplayStats(Player.Profile.Name, Player.IsReplay));
        }
    }
}