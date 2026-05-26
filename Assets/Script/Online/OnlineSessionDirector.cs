using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using YARG.Core.Logging;
using YARG.Online.Game.Contracts.Packets;
using YARG.Player;

namespace YARG.Online
{
    /// <summary>
    /// Orchestrates the gameplay-side of an online session: builds the per-peer player list,
    /// routes inbound wire events to remote simulators, and forwards local engine events over the wire.
    /// </summary>
    public sealed class OnlineSessionDirector : IDisposable
    {
        /// <summary>The active director, or null if no online game is in flight.</summary>
        public static OnlineSessionDirector Current { get; private set; }

        private readonly GameClientSession _session;

        private readonly Dictionary<int, YargPlayer> _peerToPlayer = new();
        private List<YargPlayer> _orderedPlayers = new();

        private readonly Dictionary<int, YARG.Core.Engine.Prediction.IRemotePlayerSimulator> _remoteSimulators = new();
        private YARG.Core.Engine.BaseEngine _localEngineForStats;

        // Wire events queue from the receive thread to the main thread for dispatch.
        // Engine/simulator APIs are not thread-safe and touch Unity Material APIs.
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingWireEvent> _pendingWireEvents = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingSnapshot> _pendingSnapshots = new();

        // Buffer for wire events arriving before RegisterRemoteSimulator (race between
        // receive thread and gameplay scene init). Flushed on registration so no events
        // are dropped. Snapshot payloads are carried inline to preserve FIFO pairing.
        private readonly struct DeferredEvent
        {
            public DeferredEvent(PendingWireEvent wireEvent, PendingSnapshot? snapshot)
            {
                WireEvent = wireEvent;
                Snapshot  = snapshot;
            }
            public readonly PendingWireEvent WireEvent;
            public readonly PendingSnapshot? Snapshot;
        }
        private readonly Dictionary<int, Queue<DeferredEvent>> _peerPendingEvents = new();
        private const int MaxPeerPendingEvents = 1024;

        private readonly struct PendingSnapshot
        {
            public PendingSnapshot(int peerId, double songTime, byte snapshotKind, byte[] snapshotData)
            {
                PeerId = peerId;
                SongTime = songTime;
                SnapshotKind = snapshotKind;
                SnapshotData = snapshotData;
            }

            public readonly int    PeerId;
            public readonly double SongTime;
            public readonly byte   SnapshotKind;
            public readonly byte[] SnapshotData;
        }

        private enum WireEventKind : byte
        {
            NoteMissed = 1,
            StarPowerActivated = 2,
            Whammy = 3,
            SustainReleased = 4,
            Overstrum = 5,
            NoteHit = 6,
            EngineStateSnapshot = 7,
                VocalPitch = 8,
            FreePlayInput = 9,
        }

        private readonly struct PendingWireEvent
        {
            public PendingWireEvent(int peerId, WireEventKind kind, int noteIndex, double songTime, float value)
            {
                PeerId = peerId;
                Kind = kind;
                NoteIndex = noteIndex;
                SongTime = songTime;
                Value = value;
            }

            public readonly int          PeerId;
            public readonly WireEventKind Kind;
            public readonly int          NoteIndex;
            public readonly double       SongTime;
            public readonly float        Value;
        }
        private int _localPeerId;
        private long? _songOriginUtcMs;
        private int _countdownMs;
        private UniTaskCompletionSource _startCueTcs;
        private int _disposed;

        /// <summary>Fired (main thread) when a remote peer is marked DNF.</summary>
        public event Action<int> RemotePlayerLeft;

        /// <summary>Fired (main thread) when the session dies mid-game (GameEnd or disconnect).
        /// Bool is true if local progress existed before death.</summary>
        public event Action<bool> SessionEndedExternally;

        /// <summary>True after GameEnd or disconnect. Late-arriving paths should short-circuit.</summary>
        public bool SessionAbortedExternally { get; private set; }

        private int _localEngineEventsObserved;

        /// <summary>Players in stable order: local first, remotes sorted by peer id.</summary>
        public IReadOnlyList<YargPlayer> Players => _orderedPlayers;

        /// <summary>Local peer's server-assigned id, or 0 if inactive.</summary>
        public int LocalPeerId => _localPeerId;

        /// <summary>Wall-clock instant (unix ms UTC) for songTime=0. Null until start cue arrives.</summary>
        public long? SongOriginUtcMs => _songOriginUtcMs;

        public int StartCountdownMs => _countdownMs;

        public OnlineSessionDirector(GameClientSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _startCueTcs = new UniTaskCompletionSource();

            _session.StartCueReceived += OnStartCueReceived;
            _session.RemotePeerLeft   += OnRemotePeerLeftEvent;
            _session.GameEnded    += OnGameSessionEnded;
            _session.Disconnected += OnGameSessionDisconnected;

            _session.NoteMissedReceived          += OnWireNoteMissed;
            _session.NoteHitReceived             += OnWireNoteHit;
            _session.StarPowerActivatedReceived  += OnWireStarPowerActivated;
            _session.WhammyReceived              += OnWireWhammy;
            _session.VocalPitchReceived          += OnWireVocalPitch;
            _session.FreePlayInputReceived       += OnWireFreePlayInput;
            _session.SustainReleasedReceived     += OnWireSustainReleased;
            _session.OverstrumReceived           += OnWireOverstrum;
            _session.EngineStateSnapshotReceived += OnWireEngineStateSnapshot;

            if (Current != null)
            {
                YargLogger.LogWarning(
                    "OnlineSessionDirector: Current is already set when constructing a new instance; " +
                    "overwriting. (Did a previous director fail to Dispose?)");
            }
            Current = this;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _session.StartCueReceived -= OnStartCueReceived;
            _session.RemotePeerLeft   -= OnRemotePeerLeftEvent;
            _session.GameEnded        -= OnGameSessionEnded;
            _session.Disconnected     -= OnGameSessionDisconnected;
            _session.NoteMissedReceived          -= OnWireNoteMissed;
            _session.NoteHitReceived             -= OnWireNoteHit;
            _session.StarPowerActivatedReceived  -= OnWireStarPowerActivated;
            _session.WhammyReceived              -= OnWireWhammy;
            _session.VocalPitchReceived          -= OnWireVocalPitch;
            _session.FreePlayInputReceived       -= OnWireFreePlayInput;
            _session.SustainReleasedReceived     -= OnWireSustainReleased;
            _session.OverstrumReceived           -= OnWireOverstrum;
            _session.EngineStateSnapshotReceived -= OnWireEngineStateSnapshot;
            _remoteSimulators.Clear();

            _peerToPlayer.Clear();
            _orderedPlayers = new List<YargPlayer>();
            _localPeerId = 0;
            _songOriginUtcMs = null;
            _countdownMs = 0;
            _startCueTcs?.TrySetCanceled();
            _startCueTcs = null;

            if (Current == this) Current = null;
        }

        /// <summary>
        /// Seconds until the server-picked song origin, using the synced clock. May be negative.
        /// Returns false if the start cue hasn't arrived yet.
        /// </summary>
        public bool TryGetSongStartOffsetSeconds(out double secondsUntilOrigin)
        {
            if (_songOriginUtcMs is not long originMs)
            {
                secondsUntilOrigin = 0;
                return false;
            }
            long localNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sync = ServerClockSync.Current;
            long offsetMs;
            if (sync != null && sync.IsSynced)
            {
                offsetMs = sync.ClockOffsetMs;
            }
            else
            {
                offsetMs = 0;
                YargLogger.LogWarning(
                    "OnlineSessionDirector: ServerClockSync not available; song start uses raw local " +
                    "wall clock and may misalign across peers by however far system clocks differ.");
            }
            secondsUntilOrigin = (originMs - (localNowMs + offsetMs)) / 1000.0;
            return true;
        }

        /// <summary>Build the player list from the server's peer loadouts.</summary>
        public void RegisterSession(PeerLoadout[] loadouts)
        {
            var localUserId = LobbyHubSession.Current?.LocalUserId;
            var localPlayer = PlayerContainer.Players.Count > 0 ? PlayerContainer.Players[0] : null;

            PeerLoadout localLoadout = null;
            foreach (var l in loadouts)
            {
                if (l.UserId == localUserId)
                {
                    _localPeerId = l.PeerId;
                    localLoadout = l;
                    if (localPlayer != null)
                    {
                        _peerToPlayer[l.PeerId] = localPlayer;
                        _orderedPlayers.Add(localPlayer);
                    }
                    break;
                }
            }
            if (localLoadout == null)
            {
                YargLogger.LogError(
                    $"OnlineSessionDirector: no PeerLoadout matched local user id {localUserId}; " +
                    "remote inputs from this session will fan out without a local player.");
            }

            var remotes = new List<PeerLoadout>();
            foreach (var l in loadouts)
            {
                if (l.UserId != localUserId) remotes.Add(l);
            }
            remotes.Sort((a, b) => a.PeerId.CompareTo(b.PeerId));
            foreach (var l in remotes)
            {
                var remotePlayer = new YargPlayer(l.PeerId, l);
                _peerToPlayer[l.PeerId] = remotePlayer;
                _orderedPlayers.Add(remotePlayer);
            }

            YargLogger.LogInfo(
                $"OnlineSessionDirector: registered session -- localPeerId={_localPeerId}, " +
                $"players={_orderedPlayers.Count} (1 local + {remotes.Count} remote)");
        }

        /// <summary>Completes when the server's GameStartCue arrives.</summary>
        public UniTask WaitForStartCueAsync(CancellationToken ct = default)
        {
            if (_songOriginUtcMs.HasValue || _startCueTcs == null)
            {
                return UniTask.CompletedTask;
            }
            return _startCueTcs.Task.AttachExternalCancellation(ct);
        }

        /// <summary>Subscribe the local engine's events for network fan-out. Idempotent.</summary>
        public void AttachLocalEngineForSync(YARG.Core.Engine.BaseEngine engine)
        {
            if (engine == null)
            {
                throw new ArgumentNullException(nameof(engine));
            }

            _localEngineForStats = engine;

            engine.OnSyncNoteMissed         += OnLocalEngineNoteMissed;
            engine.OnSyncNoteHit            += OnLocalEngineNoteHit;
            engine.OnSyncStarPowerActivated += OnLocalEngineStarPowerActivated;
            engine.OnSyncWhammyAxis         += OnLocalEngineWhammy;
            engine.OnSyncSustainReleased    += OnLocalEngineSustainReleased;
            engine.OnSyncOverstrum          += OnLocalEngineOverstrum;

            _lastSentNoteOutcome = null;
            _lastSnapshotSongTime    = double.NegativeInfinity;
            _lastNoteIndexSeen       = -1;
            _lastNoteIndexChangeTime = double.NegativeInfinity;
            _localGameCompleteSent = false;

            YargLogger.LogInfo(
                $"Prediction[local-attach]: wired local engine -- localPeerId={_localPeerId}, engine={engine.GetType().Name}");
        }

        // RLE note outcome tracking: true=hit run, false=miss run, null=start of song.
        private bool? _lastSentNoteOutcome;

        private void OnLocalEngineNoteMissed(int noteIndex)
        {
            _localEngineEventsObserved++;

            if (_lastSentNoteOutcome == false)
            {
                YargLogger.LogFormatTrace(
                    "Prediction[local-send] NoteMissed (suppressed -- same run): peer={0} noteIndex={1}",
                    _localPeerId, noteIndex);
                return;
            }
            _lastSentNoteOutcome = false;

            double hitTime = _localEngineForStats?.CurrentTime ?? 0.0;
            YargLogger.LogFormatDebug(
                "Prediction[local-send] NoteMissed (transition): peer={0} noteIndex={1} hitTime={2:0.000}",
                _localPeerId, noteIndex, hitTime);
            _session.SendNoteMissed(noteIndex, hitTime);
        }

        private void OnLocalEngineNoteHit(int noteIndex)
        {
            _localEngineEventsObserved++;

            if (_lastSentNoteOutcome == true)
            {
                YargLogger.LogFormatTrace(
                    "Prediction[local-send] NoteHit (suppressed -- same run): peer={0} noteIndex={1}",
                    _localPeerId, noteIndex);
                return;
            }
            _lastSentNoteOutcome = true;

            double hitTime = _localEngineForStats?.CurrentTime ?? 0.0;
            YargLogger.LogFormatDebug(
                "Prediction[local-send] NoteHit (transition): peer={0} noteIndex={1} hitTime={2:0.000}",
                _localPeerId, noteIndex, hitTime);
            _session.SendNoteHit(noteIndex, hitTime);
        }

        private void OnLocalEngineStarPowerActivated(double songTime)
        {
            YargLogger.LogFormatDebug(
                "Prediction[local-send] StarPowerActivated: peer={0} songTime={1:0.000}",
                _localPeerId, songTime);
            _session.SendStarPowerActivated(songTime);
        }

        private void OnLocalEngineWhammy(double songTime, float value)
        {
            YargLogger.LogFormatDebug(
                "Prediction[local-send] Whammy: peer={0} songTime={1:0.000} value={2:0.00}",
                _localPeerId, songTime, value);
            _session.SendWhammy(songTime, value);
        }

        /// <summary>Publish a local vocal pitch sample. Caller must rate-limit (~20 Hz).</summary>
        public void SendLocalVocalPitch(double songTime, float pitchMidi, bool isSinging)
        {
            _session.SendVocalPitch(songTime, pitchMidi, isSinging);
        }

        /// <summary>Broadcast a free-play input for remote highway visuals. Every call sends a packet.</summary>
        public void SendLocalFreePlayInput(double songTime, int action, float velocity)
        {
            _session.SendFreePlayInput(songTime, action, velocity);
        }

        /// <summary>Fires on remote free-play input. Args: peerId, songTime, action, velocity.</summary>
        public event Action<int, double, int, float> RemoteFreePlayInput;

        private void OnLocalEngineSustainReleased(int noteIndex, double songTime)
        {
            YargLogger.LogFormatDebug(
                "Prediction[local-send] SustainReleased: peer={0} noteIndex={1} songTime={2:0.000}",
                _localPeerId, noteIndex, songTime);
            _session.SendSustainReleased(noteIndex, songTime);
        }

        private void OnLocalEngineOverstrum(double songTime)
        {
            YargLogger.LogFormatDebug(
                "Prediction[local-send] Overstrum: peer={0} songTime={1:0.000}",
                _localPeerId, songTime);
            _session.SendOverstrum(songTime);
        }

        /// <summary>Register a remote peer's simulator. Replays any buffered events.</summary>
        public void RegisterRemoteSimulator(int peerId, YARG.Core.Engine.Prediction.IRemotePlayerSimulator simulator)
        {
            if (simulator == null) throw new ArgumentNullException(nameof(simulator));
            _remoteSimulators[peerId] = simulator;

            int replayed = 0;
            int replayedSnapshots = 0;
            if (_peerPendingEvents.Remove(peerId, out var pending))
            {
                while (pending.Count > 0)
                {
                    var deferred = pending.Dequeue();
                    if (deferred.Snapshot.HasValue)
                    {
                        _pendingSnapshots.Enqueue(deferred.Snapshot.Value);
                        replayedSnapshots++;
                    }
                    _pendingWireEvents.Enqueue(deferred.WireEvent);
                    replayed++;
                }
            }

            YargLogger.LogInfo(
                $"Prediction[director-register] simulator registered for peerId={peerId} engine={simulator.Engine.GetType().Name} replayedPendingEvents={replayed} (snapshots={replayedSnapshots})");
        }

        public void UnregisterRemoteSimulator(int peerId)
        {
            if (_remoteSimulators.Remove(peerId))
            {
                YargLogger.LogFormatInfo(
                    "Prediction[director-unregister] simulator removed for peerId={0}",
                    peerId);
            }
        }

        /// <summary>Tick the remote peer's simulator. No-op if unregistered.</summary>
        public void TickRemoteSimulator(int peerId, double localSongTime)
        {
            DrainWireEvents();

            if (_remoteSimulators.TryGetValue(peerId, out var sim))
            {
                sim.Update(localSongTime);
            }
        }

        /// <summary>Most-recent whammy value [0,1] for the remote peer's mirror engine.</summary>
        public float GetRemoteWhammyValue(int peerId)
        {
            return _remoteSimulators.TryGetValue(peerId, out var sim)
                ? sim.LatestWhammyValue
                : 0f;
        }

        /// <summary>Interpolated vocal pitch for a remote singer. Returns (0, false) if unregistered.</summary>
        public (float pitchMidi, bool isSinging) GetRemoteVocalPitch(int peerId, double currentSongTime)
        {
            return _remoteSimulators.TryGetValue(peerId, out var sim)
                ? sim.GetInterpolatedPitch(currentSongTime)
                : (0f, false);
        }

        /// <summary>Latest local song time, updated per frame for routing decisions.</summary>
        private volatile float _latestLocalSongTimeFloat;
        public double LatestLocalSongTime => _latestLocalSongTimeFloat;
        public void SetLatestLocalSongTime(double t) => _latestLocalSongTimeFloat = (float) t;

        // Wire handlers: enqueue only -- dispatch happens in DrainWireEvents on main thread.

        private void OnWireNoteMissed(int peerId, int noteIndex, double songTime)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.NoteMissed, noteIndex, songTime, 0f));
        }

        private void OnWireStarPowerActivated(int peerId, double songTime)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.StarPowerActivated, 0, songTime, 0f));
        }

        private void OnWireWhammy(int peerId, double songTime, float value)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.Whammy, 0, songTime, value));
        }

        private void OnWireVocalPitch(int peerId, double songTime, float pitchMidi, bool isSinging)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.VocalPitch, isSinging ? 1 : 0, songTime, pitchMidi));
        }

        private void OnWireFreePlayInput(int peerId, double songTime, int action, float velocity)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.FreePlayInput, action, songTime, velocity));
        }

        private void OnWireSustainReleased(int peerId, int noteIndex, double songTime)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.SustainReleased, noteIndex, songTime, 0f));
        }

        private void OnWireOverstrum(int peerId, double songTime)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.Overstrum, 0, songTime, 0f));
        }

        private void OnWireNoteHit(int peerId, int noteIndex, double songTime)
        {
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.NoteHit, noteIndex, songTime, 0f));
        }

        private void OnWireEngineStateSnapshot(int peerId, double songTime, byte snapshotKind, byte[] snapshotData)
        {
            _pendingSnapshots.Enqueue(new PendingSnapshot(peerId, songTime, snapshotKind, snapshotData));
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.EngineStateSnapshot, 0, songTime, 0f));
        }

        private void DrainWireEvents()
        {
            while (_pendingWireEvents.TryDequeue(out var ev))
            {
                if (ev.Kind == WireEventKind.FreePlayInput)
                {
                    RemoteFreePlayInput?.Invoke(ev.PeerId, ev.SongTime, ev.NoteIndex, ev.Value);
                    continue;
                }

                if (!_remoteSimulators.TryGetValue(ev.PeerId, out var sim))
                {
                    // Continuous-stream events don't need buffering -- latest replaces prior.
                    if (ev.Kind == WireEventKind.Whammy || ev.Kind == WireEventKind.VocalPitch)
                    {
                        continue;
                    }

                    PendingSnapshot? snap = null;
                    if (ev.Kind == WireEventKind.EngineStateSnapshot &&
                        _pendingSnapshots.TryDequeue(out var snapPayload))
                    {
                        snap = snapPayload;
                    }

                    if (!_peerPendingEvents.TryGetValue(ev.PeerId, out var peerQueue))
                    {
                        peerQueue = new Queue<DeferredEvent>();
                        _peerPendingEvents[ev.PeerId] = peerQueue;
                    }
                    if (peerQueue.Count >= MaxPeerPendingEvents)
                    {
                        peerQueue.Dequeue();
                    }
                    peerQueue.Enqueue(new DeferredEvent(ev, snap));
                    continue;
                }

                switch (ev.Kind)
                {
                    case WireEventKind.NoteMissed:
                        sim.OnNoteMissed(ev.NoteIndex, LatestLocalSongTime);
                        break;
                    case WireEventKind.StarPowerActivated:
                        sim.OnStarPowerActivated(ev.SongTime, LatestLocalSongTime);
                        break;
                    case WireEventKind.Whammy:
                        sim.OnWhammy(ev.SongTime, ev.Value, LatestLocalSongTime);
                        break;
                    case WireEventKind.VocalPitch:
                        sim.OnVocalPitch(ev.SongTime, ev.Value, ev.NoteIndex != 0, LatestLocalSongTime);
                        break;
                    case WireEventKind.SustainReleased:
                        sim.OnSustainReleased(ev.NoteIndex, ev.SongTime, LatestLocalSongTime);
                        break;
                    case WireEventKind.Overstrum:
                        sim.OnOverstrum(ev.SongTime, LatestLocalSongTime);
                        break;
                    case WireEventKind.NoteHit:
                        sim.OnNoteHit(ev.NoteIndex, LatestLocalSongTime);
                        sim.RecordWireHitOffset(ev.NoteIndex, ev.SongTime);
                        break;
                    case WireEventKind.EngineStateSnapshot:
                    {
                        if (!_pendingSnapshots.TryDequeue(out var snap))
                        {
                            YargLogger.LogWarning(
                                "Prediction[director-dispatch] EngineStateSnapshot sentinel without payload.");
                            break;
                        }
                        try
                        {
                            var decoded = EngineSnapshotSerializer.Deserialize(snap.SnapshotData, snap.SnapshotKind);
                            sim.OnEngineStateSnapshot(decoded, snap.SongTime);
                        }
                        catch (Exception ex)
                        {
                            YargLogger.LogException(ex,
                                $"Prediction[director-dispatch] failed to deserialize/apply snapshot from peerId={snap.PeerId} kind={snap.SnapshotKind} bytes={snap.SnapshotData?.Length ?? 0}");
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>Signal peer ready to the server.</summary>
        public void SendPeerReady() => _session.SendPeerReady();

        /// <summary>Signal game complete. Sends a final snapshot first.</summary>
        public void SendGameComplete()
        {
            SendSnapshotNow();
            _session.SendGameComplete();
            _localGameCompleteSent = true;
        }

        private bool _localGameCompleteSent;

        /// <summary>True once every remote peer has delivered a final snapshot past the chart length.</summary>
        public bool AllRemoteFinalSnapshotsReceived(double chartLengthSeconds)
        {
            foreach (var kv in _remoteSimulators)
            {
                if (kv.Value.AuthoritativeSnapshotSongTime < chartLengthSeconds)
                {
                    return false;
                }
            }
            return true;
        }

        // --- Periodic snapshot sender ---------------------------------

        public const double SnapshotIntervalSeconds = 0.5;
        private const double QuietHeartbeatSeconds = 5.0;
        private const double QuietZoneSeconds = 1.5;

        private double _lastSnapshotSongTime = double.NegativeInfinity;
        private int    _lastNoteIndexSeen = -1;
        private double _lastNoteIndexChangeTime = double.NegativeInfinity;

        /// <summary>Send a snapshot if enough time has elapsed. Called per-frame.</summary>
        public void MaybeSendPeriodicSnapshot(double localSongTime)
        {
            var engine = _localEngineForStats;
            if (engine == null) return;
            _ = localSongTime;

            // Skip pre-tick snapshots (CurrentTime is double.MinValue before first Update).
            if (engine.CurrentTime <= double.MinValue / 2) return;

            if (engine.CurrentTime - _lastSnapshotSongTime < SnapshotIntervalSeconds) return;

            if (engine.NoteIndex != _lastNoteIndexSeen)
            {
                _lastNoteIndexSeen = engine.NoteIndex;
                _lastNoteIndexChangeTime = engine.CurrentTime;
            }

            // Skip snapshots during quiet stretches unless heartbeat is due.
            bool noteResolvedRecently = engine.CurrentTime - _lastNoteIndexChangeTime <= QuietZoneSeconds;
            bool hasActiveState = engine.ActiveSustainCount > 0
                || engine.BaseStats.IsStarPowerActive;
            bool heartbeatDue = engine.CurrentTime - _lastSnapshotSongTime >= QuietHeartbeatSeconds;
            if (!noteResolvedRecently && !hasActiveState && !heartbeatDue)
            {
                return;
            }

            SendSnapshotNow();
        }

        private void SendSnapshotNow()
        {
            var engine = _localEngineForStats;
            if (engine == null) return;

            var snapshot = engine.CreateSnapshot();
            byte kind = EngineSnapshotSerializer.KindFor(snapshot);
            byte[] data = EngineSnapshotSerializer.Serialize(snapshot);
            _session.SendEngineStateSnapshot(engine.CurrentTime, kind, data);
            _lastSnapshotSongTime = engine.CurrentTime;
        }

        private void OnStartCueReceived(long originUtcMs, int countdownMs)
        {
            _songOriginUtcMs = originUtcMs;
            _countdownMs = countdownMs;
            _startCueTcs?.TrySetResult();
        }

        private void OnRemotePeerLeftEvent(int peerId)
        {
            if (_peerToPlayer.TryGetValue(peerId, out var player))
            {
                player.SittingOut = true;
                YargLogger.LogWarning(
                    $"OnlineSessionDirector: remote peer {peerId} ({player.Profile?.Name}) left -- marked SittingOut.");
            }
            else
            {
                YargLogger.LogFormatWarning(
                    "OnlineSessionDirector: remote peer {0} left but no YargPlayer mapping.",
                    peerId);
            }
            RemotePlayerLeft?.Invoke(peerId);
            // Don't raise SessionEndedExternally -- local player can finish solo.
        }

        private void OnGameSessionEnded()
        {
            // If we already sent GameComplete, this is the expected "all done" broadcast.
            if (_localGameCompleteSent)
            {
                YargLogger.LogInfo("OnlineSessionDirector: GameEnded received (expected -- we sent GameComplete); skipping bail-out signal.");
                SessionAbortedExternally = true;
                return;
            }
            YargLogger.LogInfo("OnlineSessionDirector: GameEnded received from server -- firing SessionEndedExternally.");
            RaiseSessionEndedExternally();
        }

        private void OnGameSessionDisconnected()
        {
            if (_localGameCompleteSent)
            {
                YargLogger.LogInfo("OnlineSessionDirector: UDP disconnected (expected -- we sent GameComplete); skipping bail-out signal.");
                SessionAbortedExternally = true;
                return;
            }
            YargLogger.LogInfo("OnlineSessionDirector: UDP transport disconnected -- firing SessionEndedExternally.");
            RaiseSessionEndedExternally();
        }

        // Idempotent -- GameEnded and Disconnected can race.
        private void RaiseSessionEndedExternally()
        {
            if (SessionAbortedExternally) return;
            SessionAbortedExternally = true;
            bool hadLocalProgress = _localEngineEventsObserved > 0;
            SessionEndedExternally?.Invoke(hadLocalProgress);
        }
    }
}
