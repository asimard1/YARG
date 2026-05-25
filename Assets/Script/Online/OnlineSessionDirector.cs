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
    /// Orchestrates the gameplay-side of an online session: builds the per-peer
    /// <see cref="YargPlayer"/> list (local first, remotes sorted by peer id),
    /// owns the per-peer <see cref="YARG.Core.Engine.Prediction.IRemotePlayerSimulator"/>
    /// registry, and routes inbound wire events from <see cref="GameClientSession"/>
    /// to the matching simulator. Also forwards the local engine's sync events
    /// (note hits/misses, sustain releases, overstrums, SP activations, whammy,
    /// and periodic engine-state snapshots) back over the wire for fan-out.
    ///
    /// One director instance per game. Constructed by <see cref="LobbyGameOrchestrator"/>
    /// alongside the session it wraps, and disposed by the orchestrator before the session
    /// is torn down. <see cref="Current"/> exists only as a handoff slot for the gameplay
    /// scene's MonoBehaviours (which can't take a ctor reference across the menu→gameplay
    /// scene boundary) — set in the constructor, cleared in <see cref="Dispose"/>.
    /// </summary>
    public sealed class OnlineSessionDirector : IDisposable
    {
        /// <summary>The active director instance, or null if no online game is in flight.
        /// Set by the constructor, cleared by <see cref="Dispose"/>. Gameplay-scene
        /// MonoBehaviours (GameManager, BasePlayer) read this once during scene init and
        /// cache the reference for the lifetime of the scene.</summary>
        public static OnlineSessionDirector Current { get; private set; }

        private readonly GameClientSession _session;

        private readonly Dictionary<int, YargPlayer> _peerToPlayer = new();
        private List<YargPlayer> _orderedPlayers = new();

        // Per-remote-peer simulator. The gameplay scene registers each remote
        // BasePlayer's engine + note projection through RegisterRemoteSimulator
        // once the engine is constructed; events arriving on _session get
        // dispatched here by peer id. Null until the gameplay scene starts
        // registering simulators.
        private readonly Dictionary<int, YARG.Core.Engine.Prediction.IRemotePlayerSimulator> _remoteSimulators = new();

        // Reference to the local player's authoritative engine. Captured by
        // AttachLocalEngineForSync. Used by MaybeSendPeriodicSnapshot to
        // capture the canonical engine state and forward it to remote peers
        // as the source-of-truth EngineStateSnapshot packet.
        private YARG.Core.Engine.BaseEngine _localEngineForStats;

        // Wire events arrive on the LiteNetLib receive thread; the simulator
        // and the YARG.Core engine it wraps are NOT thread-safe — they mutate
        // engine state, fire OnSustainEnd/OnNoteHit/etc. into the gameplay
        // scene, and those handlers touch Unity Material APIs that throw if
        // called off the main thread (UnityException:
        // GetFirstPropertyNameIdByAttribute can only be called from the main
        // thread). Marshal everything through this thread-safe queue and
        // drain in TickRemoteSimulator (which runs on the Unity main thread
        // via BasePlayer.UpdateInputs).
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingWireEvent> _pendingWireEvents = new();

        // Packet-typed queue for engine-state snapshots. Drained alongside
        // the primitive wire-event queue inside DrainWireEvents — the
        // sentinel in _pendingWireEvents triggers a pop from this list.
        private readonly System.Collections.Concurrent.ConcurrentQueue<PendingSnapshot> _pendingSnapshots = new();

        // Per-peer holding pen for wire events that arrived BEFORE the
        // peer's simulator was registered. Cause: the receive thread starts
        // queueing packets as soon as the UDP session connects, but each
        // remote player's RegisterRemoteSimulator runs from BasePlayer.Start
        // / TrackPlayer.FinishInitialization later in the gameplay scene's
        // init sequence. Without buffering, every event that arrived in
        // that window — including the very first snapshot, which carries
        // the authoritative engine state — got silently dropped in
        // DrainWireEvents, and subsequent snapshots arriving for the same
        // un-registered peer kept getting dropped too. That's the "1/10
        // times the sync just doesn't work" symptom: a deterministic-
        // looking flake whose probability tracks the random ordering of
        // GameObject.Start calls against the network thread.
        //
        // On RegisterRemoteSimulator we flush this peer's pending queue
        // back into the main wire-event queue so the normal drain dispatches
        // them in arrival order. The bounded size (MaxPeerPendingEvents)
        // caps memory if a peer never actually registers (e.g. malicious
        // server with a peer id no client knows).
        //
        // EngineStateSnapshot events are special: the sentinel in
        // _pendingWireEvents pairs FIFO-with a payload in _pendingSnapshots.
        // To preserve that pairing across deferral, the per-peer entry
        // carries the matching snapshot blob inline — dequeued from
        // _pendingSnapshots at buffer time, re-enqueued to it at flush
        // time. Cross-peer snapshot ordering doesn't matter (each snapshot
        // is per-peer authoritative); only the within-peer order is
        // preserved by the FIFO queue.
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
            // PendingWireEvent.Value carries pitchMidi; NoteIndex (0|1) carries isSinging.
            // Stored this way to avoid widening the struct just for one bool field.
            VocalPitch = 8,
            // PendingWireEvent.NoteIndex carries the instrument-action enum
            // value; Value carries velocity (drums only; guitar passes 0).
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

        /// <summary>Fired (Unity main thread) after a remote peer's slot has been marked DNF
        /// in response to <see cref="GameClientSession.RemotePeerLeft"/>. The gameplay scene
        /// can react by freezing the relevant highway.</summary>
        public event Action<int> RemotePlayerLeft;

        /// <summary>Fired (Unity main thread) when the underlying session dies mid-game —
        /// either the server sent GameEnd (straggler timeout, all-complete) or the UDP
        /// transport disconnected. The gameplay scene should bail out of the song
        /// gracefully (no fake-online single-player run-out) and route the user back
        /// to the lobby or score screen. The bool argument is true when at least one
        /// note had been resolved locally before the session died — used by the bail
        /// path to decide between "save partial run" vs "abort cleanly".</summary>
        public event Action<bool> SessionEndedExternally;

        /// <summary>True after either the GameEnd packet was received from the server
        /// or the UDP transport reported a disconnect. Latched by the wire handlers,
        /// cleared on Dispose. Lets late-arriving paths (e.g. CreatePlayers finishing
        /// after the session already died) short-circuit instead of registering
        /// engines into a torn-down director.</summary>
        public bool SessionAbortedExternally { get; private set; }

        // Tracks whether any local engine event fired before the session died, so
        // bail-out callers can route partial-progress runs differently from a clean
        // "session died before any play happened" abort.
        private int _localEngineEventsObserved;

        /// <summary>Players for the current session in stable order: local first, remotes
        /// sorted by ascending peer id (so each peer's gameplay scene assigns the same
        /// highway slots).</summary>
        public IReadOnlyList<YargPlayer> Players => _orderedPlayers;

        /// <summary>The local peer's server-assigned id, or 0 if no session is active.</summary>
        public int LocalPeerId => _localPeerId;

        /// <summary>The wall-clock instant (unix ms, UTC) the server picked for songTime=0.
        /// Set when <see cref="GameClientSession.StartCueReceived"/> fires; null until then.
        /// GameManager awaits <see cref="UniTask"/>.Delay until wall-clock reaches this
        /// instant, then enables the update loop at SongTime=0 so every client begins
        /// playback on the same wall-clock instant (within local clock skew).</summary>
        public long? SongOriginUtcMs => _songOriginUtcMs;

        public int StartCountdownMs => _countdownMs;

        public OnlineSessionDirector(GameClientSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _startCueTcs = new UniTaskCompletionSource();

            _session.StartCueReceived += OnStartCueReceived;
            _session.RemotePeerLeft   += OnRemotePeerLeftEvent;
            // Mid-session death signals — server-broadcast GameEnd (straggler
            // timeout, all-complete-broadcast, force-end) and UDP transport
            // disconnect. Both fire SessionEndedExternally so GameManager can
            // bail out of the gameplay scene gracefully instead of playing
            // the song through as a fake-offline run.
            _session.GameEnded    += OnGameSessionEnded;
            _session.Disconnected += OnGameSessionDisconnected;

            // Prediction-event fanout: route inbound per-event packets and
            // the periodic EngineStateSnapshot to the matching peer's
            // RemotePlayerSimulator. Subscribers fire on the LiteNetLib
            // receive thread; the simulator mutates engine state and the
            // gameplay update loop ticks it on the Unity main thread. The
            // engine itself isn't yet thread-safe for free-running access,
            // so we hop to the main thread for the commits by deferring
            // through the player update — but the event *handler* runs on
            // the receive thread.
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
        /// Returns the seconds remaining until the server-picked
        /// <see cref="SongOriginUtcMs"/> as measured against the synced server clock
        /// (<see cref="ServerClockSync.Current"/>). Callers should
        /// <c>await UniTask.Delay(secondsUntilOrigin)</c> before enabling gameplay so
        /// every peer begins playback at the same instant on the server's clock.
        /// May be negative if the cue arrived after the origin (callers should start
        /// without waiting in that case). Returns false if the start cue hasn't arrived
        /// yet — callers should fall back to a local-clock start.
        ///
        /// If <see cref="ServerClockSync.Current"/> is null or not yet synced, falls
        /// back to comparing against the raw local wall clock and logs a warning —
        /// alignment across peers will only be as good as their NTP agreement.
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

        /// <summary>
        /// Called by <see cref="LobbyGameOrchestrator"/> on receipt of GameStart (which
        /// includes every peer's loadout). Builds the player list and per-peer buffers.
        /// </summary>
        public void RegisterSession(PeerLoadout[] loadouts)
        {
            var localUserId = LobbyHubSession.Current?.LocalUserId;
            var localPlayer = PlayerContainer.Players.Count > 0 ? PlayerContainer.Players[0] : null;

            // First pass: identify the local peer (matched by user id from auth).
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

            // Second pass: build remote players, ordered by peer id (deterministic across
            // clients so each peer's slot ordering is identical).
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
                $"OnlineSessionDirector: registered session — localPeerId={_localPeerId}, " +
                $"players={_orderedPlayers.Count} (1 local + {remotes.Count} remote)");
        }

        /// <summary>
        /// UniTask that completes when the server's GameStartCue arrives. If the cue has
        /// already arrived, completes immediately.
        /// </summary>
        public UniTask WaitForStartCueAsync(CancellationToken ct = default)
        {
            if (_songOriginUtcMs.HasValue || _startCueTcs == null)
            {
                return UniTask.CompletedTask;
            }
            return _startCueTcs.Task.AttachExternalCancellation(ct);
        }

        /// <summary>
        /// Wire the local player's engine into the network-sync event stream:
        /// every miss/SP activation/whammy sample the engine emits gets
        /// forwarded as a packet for fanout to other peers.
        ///
        /// Idempotent against the same engine reference. Call after the engine
        /// is constructed in <see cref="BasePlayer"/> initialization. The
        /// director does not hold the engine — it only subscribes; the
        /// engine's lifetime is owned by the player.
        /// </summary>
        public void AttachLocalEngineForSync(YARG.Core.Engine.BaseEngine engine)
        {
            if (engine == null)
            {
                throw new ArgumentNullException(nameof(engine));
            }

            // Each handler captures the session reference once and uses the
            // already-thread-safe send methods. Engine events fire on the
            // Unity main thread (the engine is updated from BasePlayer's
            // Unity update path); _session.Send* are safe from any thread.
            _localEngineForStats = engine;

            engine.OnSyncNoteMissed         += OnLocalEngineNoteMissed;
            engine.OnSyncNoteHit            += OnLocalEngineNoteHit;
            engine.OnSyncStarPowerActivated += OnLocalEngineStarPowerActivated;
            engine.OnSyncWhammyAxis         += OnLocalEngineWhammy;
            engine.OnSyncSustainReleased    += OnLocalEngineSustainReleased;
            engine.OnSyncOverstrum          += OnLocalEngineOverstrum;

            // Fresh sender for the new run — first hit/miss the engine reports will
            // be emitted unconditionally (initial outcome state is null), every
            // subsequent event is gated on outcome-flip below.
            _lastSentNoteOutcome = null;

            // Reset periodic-snapshot bookkeeping. -1 / -inf force the first
            // post-attach call to MaybeSendPeriodicSnapshot to capture the
            // initial NoteIndex transition and emit a snapshot immediately.
            _lastSnapshotSongTime    = double.NegativeInfinity;
            _lastNoteIndexSeen       = -1;
            _lastNoteIndexChangeTime = double.NegativeInfinity;

            // Fresh game = we have not yet sent GameComplete. This gates the
            // "expected vs unexpected GameEnd" logic in OnGameSessionEnded /
            // OnGameSessionDisconnected — without this reset, a previous
            // game's flag would carry forward and suppress the bail-out toast
            // even when the next game dies abnormally before we finish it.
            _localGameCompleteSent = false;

            YargLogger.LogInfo(
                $"Prediction[local-attach]: wired local engine — localPeerId={_localPeerId}, engine={engine.GetType().Name}");
        }

        // Run-length-encoded note hit/miss wire protocol. The receiver fills in
        // implicit-opposite-kind notes between transition packets (see
        // RemotePlayerSimulator._nextExpectedNoteIndex). Initial null is treated
        // as "no run started yet" so the very first event always sends — that
        // anchors the receiver's cursor at the right index for the first run.
        //   true  = last sent was a Hit
        //   false = last sent was a Miss
        //   null  = nothing sent yet (start of song / engine attach)
        private bool? _lastSentNoteOutcome;

        private void OnLocalEngineNoteMissed(int noteIndex)
        {
            // Track local progress for the session-ended bail flow.
            _localEngineEventsObserved++;

            // RLE gate: skip if we're already in a miss run. The receiver fills the
            // in-between notes as implicit misses on the next Hit transition packet.
            if (_lastSentNoteOutcome == false)
            {
                YargLogger.LogFormatTrace(
                    "Prediction[local-send] NoteMissed (suppressed — same run): peer={0} noteIndex={1}",
                    _localPeerId, noteIndex);
                return;
            }
            _lastSentNoteOutcome = false;

            // Send the engine's CurrentTime as the wire songTime. For misses
            // it acts as the receiver's offset anchor — without it the remote
            // can't reconstruct the player's hit-timing histogram (offsets
            // would all read 0 because the mirror engine commits each hit
            // at note.Time, not at the sender's actual input time).
            double hitTime = _localEngineForStats?.CurrentTime ?? 0.0;
            YargLogger.LogFormatDebug(
                "Prediction[local-send] NoteMissed (transition): peer={0} noteIndex={1} hitTime={2:0.000}",
                _localPeerId, noteIndex, hitTime);
            _session.SendNoteMissed(noteIndex, hitTime);
        }

        private void OnLocalEngineNoteHit(int noteIndex)
        {
            // Track local progress for the session-ended bail flow.
            _localEngineEventsObserved++;

            // RLE gate: skip if we're already in a hit run.
            if (_lastSentNoteOutcome == true)
            {
                YargLogger.LogFormatTrace(
                    "Prediction[local-send] NoteHit (suppressed — same run): peer={0} noteIndex={1}",
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

        /// <summary>
        /// Public hook for the local vocals player to publish a pitch sample. Caller is
        /// responsible for rate-limiting (~20 Hz is plenty — the receiver interpolates).
        /// Pass IsSinging so the receiver can distinguish a valid 0 MIDI from silence.
        /// </summary>
        public void SendLocalVocalPitch(double songTime, float pitchMidi, bool isSinging)
        {
            _session.SendVocalPitch(songTime, pitchMidi, isSinging);
        }

        /// <summary>Public hook for local players to broadcast a raw input
        /// event during a free-play section (drum/guitar BRE, drum activator
        /// fill). Receivers fire <see cref="RemoteFreePlayInput"/> for the
        /// matching peer so the remote highway's visual layer can flash the
        /// corresponding pad / fret without involving the mirror engine's
        /// score state. Caller is responsible for gating to the right sections
        /// — every call results in a wire packet.</summary>
        public void SendLocalFreePlayInput(double songTime, int action, float velocity)
        {
            _session.SendFreePlayInput(songTime, action, velocity);
        }

        /// <summary>Fires (peerId, songTime, action, velocity) when a free-play
        /// input packet arrives. Drum/guitar player scripts subscribe to drive
        /// pad/fret flash visuals on the remote highway. Runs on the Unity
        /// main thread via the director's drain.</summary>
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

        /// <summary>
        /// Register a <see cref="YARG.Core.Engine.Prediction.IRemotePlayerSimulator"/>
        /// for a remote peer. Called by the gameplay scene's remote BasePlayer
        /// after CreateEngine returns, so inbound miss/SP/whammy/sustain
        /// events for that peer drive the simulator's mirror engine.
        /// </summary>
        public void RegisterRemoteSimulator(int peerId, YARG.Core.Engine.Prediction.IRemotePlayerSimulator simulator)
        {
            if (simulator == null) throw new ArgumentNullException(nameof(simulator));
            _remoteSimulators[peerId] = simulator;

            // Replay any events that arrived for this peer before this
            // simulator registration — the receive thread starts queueing
            // packets the moment the UDP session connects, but TrackPlayer
            // / VocalsPlayer don't call RegisterRemoteSimulator until
            // BasePlayer.Start runs later in the gameplay-scene init order.
            // Push them back onto the main queues so the next DrainWireEvents
            // tick dispatches them in arrival order. Without this flush, the
            // very first authoritative snapshot is dropped on ~10% of song
            // entries (rate tracks how the gameplay-scene Awake/Start order
            // races the network thread).
            //
            // Snapshot payloads come back through _pendingSnapshots paired
            // with their sentinels in _pendingWireEvents — same invariant
            // the wire-receive path enforces, just deferred.
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

        /// <summary>
        /// Tick the simulator for the given remote peer to <paramref name="localSongTime"/>.
        /// Called from the gameplay-scene remote BasePlayer's UpdateInputs.
        /// Safe to call when no simulator is registered — no-op in that case.
        /// </summary>
        public void TickRemoteSimulator(int peerId, double localSongTime)
        {
            // Drain wire events queued by the receive thread before advancing
            // any simulator. Doing it on every TickRemoteSimulator call is
            // wasteful when multiple remote peers exist (each peer tick
            // drains the global queue), but the queue's Dequeue is cheap
            // and the events apply to the correct sim regardless of which
            // peer's tick triggered the drain. Drain runs on the Unity main
            // thread, which is what all the OnSustainEnd / OnNoteHit /
            // OnNoteMissed handlers (and the Material APIs they invoke)
            // require.
            DrainWireEvents();

            if (_remoteSimulators.TryGetValue(peerId, out var sim))
            {
                sim.Update(localSongTime);
            }
        }

        /// <summary>
        /// Most-recent whammy axis value applied to the remote player's
        /// mirror engine, in [0,1]. Remote-player instrument scripts poll
        /// this each frame to drive their visual whammy state (sustain bar
        /// bend + stem pitch) because their engine has no OnInputQueued
        /// path that would otherwise produce it.
        /// </summary>
        public float GetRemoteWhammyValue(int peerId)
        {
            return _remoteSimulators.TryGetValue(peerId, out var sim)
                ? sim.LatestWhammyValue
                : 0f;
        }

        /// <summary>
        /// Smoothly interpolated vocal pitch for the remote singer at the given local time.
        /// Returns (pitchMidi, isSinging); when no simulator is registered for the peer
        /// (e.g. they aren't a vocalist) returns (0f, false). The visual layer reads this
        /// per-frame to position the on-track pitch blob.
        /// </summary>
        public (float pitchMidi, bool isSinging) GetRemoteVocalPitch(int peerId, double currentSongTime)
        {
            return _remoteSimulators.TryGetValue(peerId, out var sim)
                ? sim.GetInterpolatedPitch(currentSongTime)
                : (0f, false);
        }

        /// <summary>
        /// Most recently known local song time on the gameplay thread. Used by
        /// the receive-thread event handlers as the "now" for routing decisions
        /// (in-window vs rollback). Updated each frame by gameplay update.
        /// </summary>
        private volatile float _latestLocalSongTimeFloat;
        public double LatestLocalSongTime => _latestLocalSongTimeFloat;
        public void SetLatestLocalSongTime(double t) => _latestLocalSongTimeFloat = (float) t;

        // Wire handlers: called on the LiteNetLib receive thread. ONLY
        // enqueue here — never touch _remoteSimulators or the engine
        // directly. The main-thread drain in TickRemoteSimulator handles
        // dispatch.

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
            // NoteIndex re-used as a bool: 1 = singing, 0 = silent. Value carries pitchMidi.
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
            // Sentinel entry in the main queue so the drain loop has a
            // dispatch site even though the real payload lives in the
            // packet-typed queue.
            _pendingWireEvents.Enqueue(new PendingWireEvent(
                peerId, WireEventKind.EngineStateSnapshot, 0, songTime, 0f));
        }

        // Drains the wire-event queue and dispatches each to the matching
        // simulator. Caller runs on the Unity main thread.
        private void DrainWireEvents()
        {
            while (_pendingWireEvents.TryDequeue(out var ev))
            {
                // FreePlayInput is a pure visual event — no simulator state
                // changes, no per-peer scheduler. Fire the director-level
                // event before the sim lookup so it works even when the
                // sender doesn't have a registered simulator on this client
                // (which never actually happens today but keeps the path
                // independent of the prediction layer).
                if (ev.Kind == WireEventKind.FreePlayInput)
                {
                    RemoteFreePlayInput?.Invoke(ev.PeerId, ev.SongTime, ev.NoteIndex, ev.Value);
                    continue;
                }

                if (!_remoteSimulators.TryGetValue(ev.PeerId, out var sim))
                {
                    // Sim isn't registered yet — buffer per-peer so the
                    // event can be replayed once RegisterRemoteSimulator
                    // arrives. See _peerPendingEvents docs for the
                    // registration race this guards against. Whammy /
                    // VocalPitch are continuous-stream events where the
                    // latest sample fully replaces prior ones, so dropping
                    // pre-registration samples is fine — skip the buffer
                    // to save memory.
                    if (ev.Kind == WireEventKind.Whammy || ev.Kind == WireEventKind.VocalPitch)
                    {
                        continue;
                    }

                    // For EngineStateSnapshot, pull the matching payload
                    // out of _pendingSnapshots NOW and pair it with the
                    // sentinel — otherwise re-enqueueing the sentinel
                    // alone would desync the FIFO pairing for other peers'
                    // snapshots.
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
                        // Drop the oldest event rather than the new one —
                        // the freshest snapshot is the most useful to keep
                        // around for replay-on-registration.
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
                        // Wire-provided hit timing — feed the receiver's
                        // offset histogram with the sender's real input
                        // timing rather than the mirror engine's
                        // synthetic-at-note.Time commit.
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

        /// <summary>Pass-through to the wrapped session — GameManager's signal that this
        /// peer is ready to begin playback once the server's GameStartCue arrives.</summary>
        public void SendPeerReady() => _session.SendPeerReady();

        /// <summary>Pass-through to the wrapped session — GameManager's signal
        /// that this peer has finished the song. Sends one final authoritative
        /// engine-state snapshot first so receivers can snap their mirror
        /// engine to the exact terminal state before GameEnd fans out and the
        /// results screen renders.</summary>
        public void SendGameComplete()
        {
            // Force a final snapshot send regardless of the throttle so
            // receivers don't wait through the snapshot interval before
            // seeing the terminal state.
            SendSnapshotNow();
            _session.SendGameComplete();

            // Mark that our side finished the song cleanly. Any GameEnd that
            // arrives from the server after this is the expected "all peers
            // done" broadcast — not an abnormal session death — so the
            // SessionEndedExternally consumer should NOT fire the bail-out
            // toast (which would kick us off the score screen).
            _localGameCompleteSent = true;
        }

        private bool _localGameCompleteSent;

        /// <summary>
        /// True once every connected remote peer has delivered a snapshot
        /// whose <c>SongTime</c> is at or beyond <paramref name="chartLengthSeconds"/>.
        /// The local sender broadcasts its final snapshot right before
        /// GameComplete, so this flips true on every receiver once GameComplete
        /// chains through (or sooner, if snapshots arrive ahead of GameComplete).
        /// GameManager waits on this before transitioning to the results
        /// screen — with a timeout fallback for disconnected peers.
        /// </summary>
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

        // Send an engine-state snapshot every this many seconds of song time.
        // 0.5s is a good balance: drift is corrected fast enough that visual
        // anomalies are short-lived, and per-peer bandwidth stays modest
        // (a typical guitar snapshot is ~600 bytes, so ~1.2 KB/s/peer).
        public const double SnapshotIntervalSeconds = 0.5;

        // Heartbeat cadence during breaks / quiet stretches. Below the active
        // rate, but frequent enough to catch any state divergence that did
        // creep in unnoticed (e.g. a wire event silently dropped).
        private const double QuietHeartbeatSeconds = 5.0;

        // Window after the last NoteIndex transition during which we treat
        // the engine as "actively playing". Outside this window — and with
        // no sustain or SP keeping per-tick state alive — periodic snapshots
        // fall back to the heartbeat cadence.
        private const double QuietZoneSeconds = 1.5;

        private double _lastSnapshotSongTime = double.NegativeInfinity;
        private int    _lastNoteIndexSeen = -1;
        private double _lastNoteIndexChangeTime = double.NegativeInfinity;

        /// <summary>
        /// Capture + send an authoritative engine-state snapshot if enough
        /// song time has elapsed since the last send. Called from the local
        /// player's per-frame UpdateInputs path. No-op when the local engine
        /// isn't wired up.
        /// </summary>
        public void MaybeSendPeriodicSnapshot(double localSongTime)
        {
            // Throttle and store both keyed off engine.CurrentTime so the
            // gate stays consistent with the wire payload (snapshots carry
            // engine.CurrentTime as their SongTime). localSongTime is only
            // used to discover that we *might* be due — the actual decision
            // uses the engine clock the receiver will key off.
            var engine = _localEngineForStats;
            if (engine == null) return;
            _ = localSongTime; // see comment above

            // Guard against pre-tick snapshots. BaseEngine.Reset initialises
            // CurrentTime to double.MinValue and leaves it there until the
            // first Update tick. A snapshot captured in that window serialises
            // -1.79e308 as the wire songTime; the receiver's RestoreSnapshot
            // then sets _engine.CurrentTime = double.MinValue, corrupting the
            // rollback anchor and surfacing as a "huge negative number" in
            // wire logs. Wait until the engine has actually started ticking
            // before emitting any snapshot.
            if (engine.CurrentTime <= double.MinValue / 2) return;

            if (engine.CurrentTime - _lastSnapshotSongTime < SnapshotIntervalSeconds) return;

            // Track NoteIndex transitions so we can detect active play vs
            // breaks. A NoteIndex change is the cleanest cross-instrument
            // proxy for "the engine just resolved something"; during breaks
            // it sits unchanged for many seconds.
            if (engine.NoteIndex != _lastNoteIndexSeen)
            {
                _lastNoteIndexSeen = engine.NoteIndex;
                _lastNoteIndexChangeTime = engine.CurrentTime;
            }

            // Quiet-zone gate: skip the periodic snapshot if the engine isn't
            // doing anything the receiver needs reconciled — no recent note
            // resolution, no active sustain (StarPowerTickAmount changes per
            // tick while sustaining), no SP active. We still send a
            // heartbeat every QuietHeartbeatSeconds so the receiver isn't
            // stranded indefinitely if a wire event was dropped. Without
            // this, vocals' long inter-phrase gaps generate a 0.5s churn of
            // identical snapshots that re-emit the same PredictedMiss on the
            // receiver each cycle.
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

        // Capture the local engine's current state and ship it.
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
                YargLogger.LogFormatWarning(
                    "OnlineSessionDirector: remote peer {0} ({1}) left — marked SittingOut. " +
                    "_remoteSimulatorsRegistered={2} _peerToPlayerCount={3}. If this fires before " +
                    "CreatePlayers (GameManager.Loading._players null), the player would never tick " +
                    "without the post-init defensive hide added in GameManager.Loading.CreatePlayers.",
                    peerId, player.Profile?.Name, _remoteSimulators.Count, _peerToPlayer.Count);
            }
            else
            {
                YargLogger.LogFormatWarning(
                    "OnlineSessionDirector: remote peer {0} left but no YargPlayer mapping; " +
                    "RegisterSession may not have completed before the RemotePeerLeftPacket arrived.",
                    peerId);
            }
            RemotePlayerLeft?.Invoke(peerId);

            // NOTE: do NOT raise SessionEndedExternally just because all remote
            // peers have left. The local player is still in the game and should
            // be able to finish the song solo (their highway, their score, their
            // GameComplete still goes through to the server). The earlier "fire
            // bail-out on last-remote-left" logic was wrong — it ended the local
            // player's session prematurely. RemotePlayerLeft handlers
            // (GameManager hides the leaver's highway, etc.) are sufficient to
            // surface the departure to the UI.
        }

        private void OnGameSessionEnded()
        {
            // Distinguish the two reasons the server broadcasts GameEnd:
            //   1. Expected — every peer (including us) has sent GameComplete and
            //      the server's "all done" path fired BroadcastGameEnd. We've
            //      already transitioned (or are transitioning) to the results
            //      screen and DO NOT want the session-ended bail flow to kick us
            //      off it. Mark the session aborted internally (so subsequent
            //      cleanup paths short-circuit) but skip the consumer event.
            //   2. Unexpected — straggler timeout, force-end, or arriving while
            //      we're still mid-song. Fire SessionEndedExternally so
            //      GameManager bails out gracefully.
            if (_localGameCompleteSent)
            {
                YargLogger.LogInfo("OnlineSessionDirector: GameEnded received (expected — we sent GameComplete); skipping bail-out signal.");
                SessionAbortedExternally = true;
                return;
            }
            YargLogger.LogInfo("OnlineSessionDirector: GameEnded received from server — firing SessionEndedExternally.");
            RaiseSessionEndedExternally();
        }

        private void OnGameSessionDisconnected()
        {
            // Transport-level disconnect after a clean game completion is part of
            // the normal teardown chain (BroadcastGameEnd → DisposeSession → UDP
            // close). Same logic as GameEnded above: suppress the bail signal
            // when we already sent GameComplete, so the results screen survives
            // the transport's natural unwind.
            if (_localGameCompleteSent)
            {
                YargLogger.LogInfo("OnlineSessionDirector: UDP disconnected (expected — we sent GameComplete); skipping bail-out signal.");
                SessionAbortedExternally = true;
                return;
            }
            YargLogger.LogInfo("OnlineSessionDirector: UDP transport disconnected — firing SessionEndedExternally.");
            RaiseSessionEndedExternally();
        }

        // Idempotent — the GameEnded packet can race with the transport-level
        // Disconnected callback (server disposes the session then closes the
        // connection), and we'd be raising SessionEndedExternally twice. The
        // SessionAbortedExternally latch makes the second raise a no-op.
        private void RaiseSessionEndedExternally()
        {
            if (SessionAbortedExternally) return;
            SessionAbortedExternally = true;
            bool hadLocalProgress = _localEngineEventsObserved > 0;
            SessionEndedExternally?.Invoke(hadLocalProgress);
        }
    }
}
