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

        // Forward-walking cursor used by FindRemoteTargetNoteAt. Remote
        // VocalsPlayer can't rely on the engine's OnTargetNoteChanged event
        // (the mirror engine has no mic input, so HasSang is never set and
        // CheckSingingHit returns before firing OnTargetNoteChanged). Instead
        // we scan the chart directly at the current song time. Phrases are
        // already sorted by Time, so a monotonic forward cursor amortizes
        // the lookup to O(1) per frame in steady state.
        private int _remoteTargetPhraseCursor;

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

            // Create and start an input context for the mic. Remote players have no
            // local mic — their pitch samples arrive over the network as GameInputs.
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

            // Online sync wiring (same pattern as TrackPlayer.FinishInitialization).
            // VocalsPlayer doesn't extend TrackPlayer, so the wiring lives here.
            //   - LOCAL player: forward miss/SP/sustain sync events to peers.
            //   - REMOTE player: register a RemotePlayerSimulator<VocalNote> so
            //     inbound NoteHit/Missed events drive the mirror engine.
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
                if (LastCombo >= 2)
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

        // Send-state for rate-limited outbound vocal pitch samples. Receivers interpolate
        // between samples, so 20 Hz is plenty. Singing-state transitions always send
        // (not gated by the rate limiter) so on/off snaps cleanly on the remote.
        private const double VocalPitchSendInterval = 0.05; // seconds (~20 Hz)
        private double _lastVocalPitchSendTime = double.NegativeInfinity;
        private bool   _lastVocalPitchIsSingingSent;

        protected override void UpdateInputs(double time)
        {
            // Push all inputs from the local mic. Remote players have no local mic — their
            // pitch samples arrive over the network like any other GameInput, drained by
            // the remote-input branch in BasePlayer.UpdateInputs.
            if (!Player.IsReplay && !Player.IsRemote && _inputContext != null)
            {
                foreach (var input in _inputContext.GetInputsFromMic())
                {
                    var i = input;
                    OnGameInput(ref i);
                }
            }

            base.UpdateInputs(time);

            // Online: publish our pitch so remote peers can render the on-track blob.
            // Local + non-replay + non-remote only; the director's SendLocalVocalPitch
            // is a no-op when there's no session, so checking IsOnline here is the gate.
            if (GameManager.IsOnline && !Player.IsReplay && !Player.IsRemote)
            {
                bool isSinging = _lastSingTime.HasValue;
                bool stateChanged = isSinging != _lastVocalPitchIsSingingSent;
                bool dueForSample = isSinging && (time - _lastVocalPitchSendTime) >= VocalPitchSendInterval;

                if (stateChanged || dueForSample)
                {
                    YARG.Online.OnlineSessionDirector.Current?
                        .SendLocalVocalPitch(time, Engine.PitchSang, isSinging);
                    _lastVocalPitchSendTime       = time;
                    _lastVocalPitchIsSingingSent  = isSinging;
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

        // Find the chart's active vocal target at <paramref name="songTime"/>.
        // Walks <see cref="NoteTrack"/>'s phrase list with a monotonic cursor
        // (phrases are already sorted by Time) and returns the child sing note
        // straddling the current time, or null if we're between phrases or
        // sitting on a percussion-only beat.
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

            // Source of truth for "is this player singing right now?" and "what pitch?"
            // diverges between local and remote:
            //   - LOCAL: engine's OnSing/PitchSang are driven by the mic input loop.
            //   - REMOTE: engine isn't receiving inputs (no mic), so we read from the
            //     OnlineSessionDirector's per-peer pitch simulator. The simulator linearly
            //     interpolates between the latest two received VocalPitch packets so the
            //     blob slides smoothly between samples instead of snapping at packet cadence.
            float pitchSang;
            bool  isCurrentlySinging;
            bool  isHittingTarget;
            if (Player.IsRemote)
            {
                var director = YARG.Online.OnlineSessionDirector.Current;
                (float remotePitch, bool remoteSinging) = director != null
                    ? director.GetRemoteVocalPitch(Player.RemotePeerId, GameManager.SongTime)
                    : (0f, false);

                // The mirror engine's OnTargetNoteChanged is gated on HasSang,
                // which is never set for remote players (no mic). Compute the
                // active target by scanning the chart at the current song time
                // instead. Stash it on _lastTargetNote so the rest of the
                // UpdateSingNeedle body (lerp targets, octave shift, particle
                // gating) can stay unchanged.
                _lastTargetNote = FindRemoteTargetNoteAt(GameManager.SongTime);

                // Pitch-display state machine. Predict optimistically until the
                // wire gives us a reason to do otherwise — symmetric with how
                // the guitar/drums simulators predict hits, except here the
                // "decision" is a continuous pitch sample rather than a discrete
                // hit/miss. Three observable states from the simulator:
                //
                //   (0f, false)           - no sample received yet for this peer.
                //                           Optimistic: assume the remote is on
                //                           the chart target. (Initial gap, mic
                //                           startup, brief network blips all
                //                           land here.)
                //   (nonzero, false)      - sender explicitly stopped singing.
                //                           This is the "cut off confirmation"
                //                           — hide the needle.
                //   (nonzero, true)       - sender is actively singing. Classify
                //                           against the chart target using the
                //                           engine's PitchWindow:
                //                             on-pitch  → render at target (keep
                //                                         showing target until a
                //                                         later sample says no)
                //                             off-pitch → render at the received
                //                                         pitch literally (held
                //                                         at whatever wrong note
                //                                         they're on until they
                //                                         get back on pitch or
                //                                         stop singing)
                //
                // The "held" behavior is automatic because pitch samples arrive
                // at ~20 Hz and GetInterpolatedPitch always returns the latest
                // anchor pair; we just translate "latest sample" into one of
                // the three display modes.
                bool hasActiveTarget = _lastTargetNote is not null;
                bool noSampleYet     = remotePitch == 0f && !remoteSinging;

                if (remoteSinging && hasActiveTarget)
                {
                    float targetPitch = _lastTargetNote.PitchAtSongTime(GameManager.SongTime);
                    (float pitchDist, _) = GetPitchDistanceIgnoringOctave(targetPitch, remotePitch);
                    bool onPitch = Mathf.Abs(pitchDist) <= EngineParams.PitchWindow;

                    if (onPitch && !_lastTargetNote.IsNonPitched)
                    {
                        pitchSang       = targetPitch;
                        isHittingTarget = true;
                    }
                    else
                    {
                        pitchSang       = remotePitch;
                        isHittingTarget = false;
                    }
                    isCurrentlySinging = true;
                }
                else if (noSampleYet && hasActiveTarget)
                {
                    // No evidence yet — assume on-pitch.
                    pitchSang          = _lastTargetNote.PitchAtSongTime(GameManager.SongTime);
                    isCurrentlySinging = true;
                    isHittingTarget    = !_lastTargetNote.IsNonPitched;
                }
                else
                {
                    // Either explicit cut-off (nonzero,false) or no target —
                    // mirror the simulator's view directly. The UpdateSingNeedle
                    // branch below hides the needle when isCurrentlySinging
                    // is false.
                    pitchSang          = remotePitch;
                    isCurrentlySinging = remoteSinging;
                    isHittingTarget    = false;
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

            // Since phrases start at the note, and not sometime before it, use
            // the end times of phrases instead (where the phrase lines are). Problem
            // with this is that we still gotta account for the first phrase, so use
            // an index of -1 for that.
            while (_phraseIndex == -1 ||
                (_phraseIndex < NoteTrack.Notes.Count && NoteTrack.Notes[_phraseIndex].TimeEnd <= time))
            {
                _phraseIndex++;

                // End if that's the last note
                if (_phraseIndex >= NoteTrack.Notes.Count)
                {
                    break;
                }

                var phrase = NoteTrack.Notes[_phraseIndex];

                bool hasPercussion = false;
                uint totalTime = 0;
                foreach (var note in phrase.ChildNotes)
                {
                    if (note.IsPercussion)
                    {
                        hasPercussion = true;
                        continue;
                    }

                    totalTime += note.TotalTickLength;
                }

                _hud.SetHUDShowing(!hasPercussion);
                _percussionTrack.ShowPercussionFret(hasPercussion);
                _shouldHideNeedle = hasPercussion;
            }
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
            return (frame, Engine.EngineStats.ConstructReplayStats(Player.Profile.Name));
        }
    }
}
