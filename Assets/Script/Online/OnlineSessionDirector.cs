using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using Cysharp.Threading.Tasks;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Online.Game.Contracts.Packets;
using YARG.Player;

namespace YARG.Online
{
    /// <summary>
    /// Orchestrates the gameplay-side of an online session: builds the per-peer
    /// <see cref="YargPlayer"/> list (local first, remotes sorted by peer id), owns the
    /// per-peer <see cref="Pipe"/>s into which <see cref="GameClientSession"/>'s poll
    /// thread writes inbound <see cref="GameInput"/>-shaped records, and forwards
    /// local-player <see cref="GameInput"/>s back over the wire for fanout.
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

        // Per-peer SPSC pipe carrying GameInput-shaped records (16 bytes each, layout
        // matches the wire body of EngineInputBatchPacket). Producer: LiteNetLib poll
        // thread inside GameClientSession.OnNetworkReceive. Consumer: BasePlayer.UpdateInputs
        // on the Unity main thread. System.IO.Pipelines is SPSC-safe by construction —
        // no lock needed.
        private readonly Dictionary<int, Pipe> _peerPipes = new();
        private readonly Dictionary<int, YargPlayer> _peerToPlayer = new();
        private List<YargPlayer> _orderedPlayers = new();
        private int _localPeerId;
        private long? _songOriginUtcMs;
        private int _countdownMs;
        private UniTaskCompletionSource _startCueTcs;
        private int _disposed;

        /// <summary>Fired (Unity main thread) after a remote peer's slot has been marked DNF
        /// in response to <see cref="GameClientSession.RemotePeerLeft"/>. The gameplay scene
        /// can react by freezing the relevant highway.</summary>
        public event Action<int> RemotePlayerLeft;

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

            // Detach the sink map before completing writers so any in-flight write on the
            // poll thread either sees the old map (and the now-completed writer throws
            // InvalidOperationException, swallowed there) or the new null map (skips).
            _session.AttachEngineInputSinks(null);
            foreach (var pipe in _peerPipes.Values)
            {
                try { pipe.Writer.Complete(); }
                catch (Exception ex) { YargLogger.LogWarning($"OnlineSessionDirector: pipe writer complete — {ex.Message}"); }
            }
            _peerPipes.Clear();
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
            var sinks = new Dictionary<int, PipeWriter>(remotes.Count);
            foreach (var l in remotes)
            {
                var remotePlayer = new YargPlayer(l.PeerId, l);
                _peerToPlayer[l.PeerId] = remotePlayer;
                var pipe = new Pipe(InputPipeOptions);
                _peerPipes[l.PeerId] = pipe;
                sinks[l.PeerId] = pipe.Writer;
                _orderedPlayers.Add(remotePlayer);
            }
            // Hand the writer map to the session in one atomic swap. The poll thread
            // will start writing into these pipes on the next inbound packet.
            _session.AttachEngineInputSinks(sinks);

            YargLogger.LogInfo(
                $"OnlineSessionDirector: registered session — localPeerId={_localPeerId}, " +
                $"players={_orderedPlayers.Count} (1 local + {remotes.Count} remote)");
        }

        // Tight per-peer engine-input pipe. No backpressure (pauseWriterThreshold=0):
        // the consumer ticks every Unity frame and the producer rate is bounded
        // (<30 inputs/sec/peer typical, ~16 B each). useSynchronizationContext=false
        // because the reader-side TryRead is fully synchronous from the main thread.
        // minimumSegmentSize keeps a single batch (~16 records) in one segment so the
        // consumer's FirstSpan fast-path is taken.
        private static readonly PipeOptions InputPipeOptions = new PipeOptions(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            minimumSegmentSize: 256,
            useSynchronizationContext: false);

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
        /// Returns the <see cref="PipeReader"/> draining inbound engine inputs for a remote
        /// player, or null if the player is local/unregistered. BasePlayer.UpdateInputs
        /// calls TryRead on this each frame.
        /// </summary>
        public PipeReader GetInputReader(YargPlayer remotePlayer)
        {
            if (remotePlayer == null || !remotePlayer.IsRemote) return null;
            return _peerPipes.TryGetValue(remotePlayer.RemotePeerId, out var pipe) ? pipe.Reader : null;
        }

        /// <summary>
        /// Forwards a local-player input over the network for fanout. Called from
        /// <c>BasePlayer.OnGameInput</c> after the input has already been queued into the
        /// local engine — network send latency must never stall local play.
        /// </summary>
        public void EnqueueLocalInput(YargPlayer localPlayer, GameInput input)
        {
            if (localPlayer == null || localPlayer.IsRemote) return;
            // v1: single-input flush, no batching. Wire is ReliableOrdered; per-input
            // packets are 33 bytes total and we expect <30/sec/peer at busy moments.
            var record = new EngineInputRecord(input.Time, input.Action, input.Integer);
            _session.SendEngineInputs(new[] { record });
        }

        /// <summary>Pass-through to the wrapped session — GameManager's signal that this
        /// peer is ready to begin playback once the server's GameStartCue arrives.</summary>
        public void SendPeerReady() => _session.SendPeerReady();

        /// <summary>Pass-through to the wrapped session — GameManager's signal that this
        /// peer has finished the song.</summary>
        public void SendGameComplete() => _session.SendGameComplete();

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
                YargLogger.LogInfo(
                    $"OnlineSessionDirector: remote peer {peerId} ({player.Profile?.Name}) " +
                    "left — marked SittingOut");
            }
            if (_peerPipes.TryGetValue(peerId, out var pipe))
            {
                // Signal end-of-stream to the consumer. BasePlayer drains anything still
                // buffered, then TryRead returns IsCompleted = true and the consumer
                // stops looping. The SittingOut flag prevents the player from being
                // ticked further anyway.
                try { pipe.Writer.Complete(); }
                catch (Exception ex) { YargLogger.LogWarning($"OnlineSessionDirector: pipe writer complete (peer left) — {ex.Message}"); }
            }
            RemotePlayerLeft?.Invoke(peerId);
        }
    }
}
