using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Persistent;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// SignalR session for the lobby hub. Lifetime is scoped to the online flow
    /// (lobby browser → lobby view → online gameplay/results). Static
    /// <see cref="Current"/> holds the active instance; <see cref="InitializeAsync"/>
    /// creates one and <see cref="ShutdownAsync"/> tears it down (called from
    /// <c>OnlineMenu.Back</c> when the user leaves the online flow).
    ///
    /// Maintains the browse-scope cache (seeded by <c>OnLobbySnapshot</c>, mutated
    /// by <c>OnLobbyBatch</c>) and raises <see cref="LobbiesChanged"/> on the Unity
    /// main thread whenever the cache changes. Also tracks the lobby the local
    /// player is currently in via <see cref="CurrentLobby"/>, populated by
    /// <see cref="CreateLobbyAsync"/> / <see cref="EnterLobbyAsync"/> and mutated
    /// by the in-lobby callbacks.
    /// </summary>
    public sealed class LobbyHubSession
    {
        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Reconnecting,
        }

        // ---------- Static surface ----------

        private static int _instanceCounter;

        /// <summary>The active session, or null if the player isn't in the online flow.</summary>
        public static LobbyHubSession Current { get; private set; }

        /// <summary>Convenience for <c>Current != null</c>.</summary>
        public static bool IsActive => Current != null;

        /// <summary>
        /// Create-or-reuse the active session and start connecting. Idempotent —
        /// repeated calls share the in-flight connect task. The provider supplies
        /// the bearer token to SignalR's <c>AccessTokenProvider</c> and the local
        /// identity exposed via <see cref="LocalUserId"/> / <see cref="LocalDisplayName"/>.
        /// If a session already exists, the provided <paramref name="provider"/>
        /// is ignored — the existing session's provider stays in effect.
        /// </summary>
        public static UniTask InitializeAsync(OnlineAccessTokenProvider provider, CancellationToken ct = default)
        {
            var existing = Current;
            if (existing != null) return existing.ConnectAsync(ct);

            var session = new LobbyHubSession(provider);
            Current = session;
            return session.ConnectAsync(ct);
        }

        /// <summary>
        /// Dispose the active session if any. Idempotent (no-op when
        /// <see cref="Current"/> is null). <see cref="Current"/> is nulled
        /// synchronously so a subsequent <see cref="InitializeAsync"/> creates a
        /// fresh instance even if disposal is still in flight.
        /// </summary>
        public static async UniTask ShutdownAsync()
        {
            var session = Current;
            if (session == null) return;
            Current = null;
            await session.DisposeAsync();
        }

        // ---------- Instance state ----------

        private readonly int _instanceId;
        private readonly object _lock = new();
        private readonly Dictionary<string, LobbyDto> _byId = new();
        private long _lastSequence = -1;

        private readonly OnlineAccessTokenProvider _tokenProvider;

        private HubConnection _connection;
        private UniTask? _inflightConnect;
        private ConnectionState _state = ConnectionState.Disconnected;

        private LobbyRoomState _currentLobby;
        private LobbyGameOrchestrator _orchestrator;

        private readonly CancellationTokenSource _lifetimeCts = new();
        private int _disposing; // 0 = alive, 1 = DisposeAsync in flight or finished

        // Count of fire-and-forget Track() bodies currently in flight. DisposeAsync
        // waits for this to drain to zero before tearing down session state, so a
        // callback racing with shutdown can't touch _currentLobby after the fact.
        private int _inflightHandlers;

        private LobbyHubSession(OnlineAccessTokenProvider tokenProvider)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _instanceId = Interlocked.Increment(ref _instanceCounter);
            Application.quitting += OnApplicationQuitting;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: created");
        }

        private static void OnApplicationQuitting()
        {
            ShutdownAsync().Forget();
        }

        /// <summary>The local player's server-assigned user id (from dev auth).</summary>
        public string LocalUserId => _tokenProvider.UserId;

        /// <summary>The local player's server-assigned display name (from dev auth).</summary>
        public string LocalDisplayName => _tokenProvider.DisplayName;

        public ConnectionState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>
        /// Snapshot of the current browse cache. Returns a fresh array so callers
        /// can iterate without holding the session lock.
        /// </summary>
        public IReadOnlyList<LobbyDto> Lobbies
        {
            get
            {
                lock (_lock)
                {
                    var copy = new LobbyDto[_byId.Count];
                    int i = 0;
                    foreach (var dto in _byId.Values) copy[i++] = dto;
                    return copy;
                }
            }
        }

        /// <summary>
        /// The lobby the local player is currently in, or null if not in one.
        /// Mutated only on the main thread; UI can read directly without locking.
        /// </summary>
        public LobbyRoomState CurrentLobby => _currentLobby;

        /// <summary>
        /// Fired on the Unity main thread after a snapshot or batch is applied.
        /// </summary>
        public event Action LobbiesChanged;

        /// <summary>
        /// Fired on the Unity main thread whenever the connection state changes.
        /// </summary>
        public event Action<ConnectionState> StateChanged;

        /// <summary>
        /// Fired on the Unity main thread when <see cref="CurrentLobby"/> is
        /// replaced (entered/left/closed) or any of its in-lobby state mutates.
        /// </summary>
        public event Action CurrentLobbyChanged;

        /// <summary>
        /// Fired on the Unity main thread when the local player is kicked from
        /// the current lobby. Payload is the server-provided reason.
        /// </summary>
        public event Action<string> KickedFromLobby;

        /// <summary>
        /// Connect to the lobby hub. Idempotent — concurrent callers share the
        /// same in-flight connect task; subsequent calls on an already-connected
        /// session return immediately.
        /// </summary>
        public UniTask ConnectAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_state == ConnectionState.Connected) return UniTask.CompletedTask;
                if (_inflightConnect.HasValue) return _inflightConnect.Value;

                _inflightConnect = ConnectInternalAsync(ct);
                return _inflightConnect.Value;
            }
        }

        private async UniTask ConnectInternalAsync(CancellationToken ct)
        {
            // Link the caller's ct with the session lifetime so DisposeAsync can
            // cancel an in-flight StartAsync cleanly.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            try
            {
                SetState(ConnectionState.Connecting);

                // If we're reconnecting after a transport failure, dispose the
                // dead HubConnection before building a new one.
                HubConnection stale;
                lock (_lock) { stale = _connection; _connection = null; }
                if (stale != null)
                {
                    try { await stale.DisposeAsync(); }
                    catch (Exception ex) { YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: stale dispose — {ex.Message}"); }
                }

                var url = OnlineAccessTokenProvider.BaseUrl.TrimEnd('/') + HubRoutes.Lobby;
                _connection = new HubConnectionBuilder()
                    .WithUrl(url, options =>
                    {
                        // GetAccessTokenAsync refreshes the cached token if needed,
                        // so SignalR reconnects automatically pick up a fresh one.
                        options.AccessTokenProvider = _tokenProvider.GetAccessTokenAsync;

                        // Skip negotiation + force WebSockets-only transport so
                        // the client opens a single persistent WS connection
                        // straight to the hub without the preliminary HTTP
                        // /negotiate round trip. The negotiate handshake is
                        // what triggers sticky-session affinity in front of
                        // a load-balanced SignalR deployment — without it
                        // every subsequent HTTP poll has to land on the
                        // same backend node. Since we run a single WS
                        // connection per client and don't use long-polling
                        // / SSE fallbacks, sticky sessions buy us nothing
                        // and just block horizontal scale-out behind a
                        // round-robin LB.
                        options.SkipNegotiation = true;
                        options.Transports = HttpTransportType.WebSockets;
                    })
                    .AddJsonProtocol(o =>
                    {
                        o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                        o.PayloadSerializerOptions.Converters.Add(
                            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
                    })
                    .WithAutomaticReconnect()
                    .Build();

                // Browse-scope callbacks.
                _connection.On<LobbyDto[]>(nameof(ILobbyHubClient.OnLobbySnapshot), ApplySnapshotAsync);
                _connection.On<LobbyBatchUpdate>(nameof(ILobbyHubClient.OnLobbyBatch), ApplyBatchAsync);

                // In-lobby callbacks. Each handler is an async Task that switches
                // to the main thread before touching _currentLobby — SignalR awaits
                // the returned task, preserving FIFO message ordering on the
                // receive pipeline.
                _connection.On<PlayerJoinedEvent>(nameof(ILobbyHubClient.OnPlayerJoined), OnPlayerJoinedAsync);
                _connection.On<PlayerLeftEvent>(nameof(ILobbyHubClient.OnPlayerLeft), OnPlayerLeftAsync);
                _connection.On<LobbyClosedEvent>(nameof(ILobbyHubClient.OnLobbyClosed), OnLobbyClosedAsync);
                _connection.On<HostChangedEvent>(nameof(ILobbyHubClient.OnHostChanged), OnHostChangedAsync);
                _connection.On<PlayerKickedEvent>(nameof(ILobbyHubClient.OnPlayerKicked), OnPlayerKickedAsync);
                _connection.On<LobbyStatusChangedEvent>(nameof(ILobbyHubClient.OnLobbyStatusChanged), OnLobbyStatusChangedAsync);
                _connection.On<LobbySongLibraryUpdatedEvent>(nameof(ILobbyHubClient.OnLobbySongLibraryUpdated), OnLobbySongLibraryUpdatedAsync);
                _connection.On<SongQueuedEvent>(nameof(ILobbyHubClient.OnSongQueued), OnSongQueuedAsync);
                _connection.On<SongRemovedFromQueueEvent>(nameof(ILobbyHubClient.OnSongRemovedFromQueue), OnSongRemovedFromQueueAsync);
                _connection.On<QueuedSongAvailabilityChangedEvent>(nameof(ILobbyHubClient.OnQueuedSongAvailabilityChanged), OnQueuedSongAvailabilityChangedAsync);
                _connection.On<ChatMessageEvent>(nameof(ILobbyHubClient.OnChatMessage), OnChatMessageAsync);
                _connection.On<GameStartedEvent>(nameof(ILobbyHubClient.OnGameStarted), OnGameStartedAsync);
                _connection.On<PlayerLobbyReadyChangedEvent>(
                    nameof(ILobbyHubClient.OnPlayerLobbyReadyChanged),
                    OnPlayerLobbyReadyChangedAsync);

                _connection.Reconnecting += _ =>
                {
                    SetState(ConnectionState.Reconnecting);
                    return Task.CompletedTask;
                };
                _connection.Reconnected += async _ =>
                {
                    // Server re-sends OnLobbySnapshot on (re)connect per ILobbyHub's docstring.
                    // In-lobby state is NOT resent — if we were in a lobby, the server has
                    // already cleaned us up. Drop our local CurrentLobby so the UI can navigate
                    // back to the browser. (Rejoin-after-reconnect needs a RejoinLobby hub
                    // method that the contract doesn't define yet.)
                    try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
                    catch (OperationCanceledException) { return; }
                    if (_currentLobby != null)
                    {
                        YargLogger.LogWarning(
                            $"LobbyHubSession[#{_instanceId}]: reconnected while in a lobby — clearing CurrentLobby "
                            + "(server-side rejoin not implemented).");
                        _currentLobby = null;
                        CurrentLobbyChanged?.Invoke();
                    }
                    SetState(ConnectionState.Connected);
                };
                _connection.Closed += async _ =>
                {
                    try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
                    catch (OperationCanceledException) { return; }
                    if (_currentLobby != null)
                    {
                        _currentLobby = null;
                        CurrentLobbyChanged?.Invoke();
                    }
                    SetState(ConnectionState.Disconnected);
                };

                YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: connecting to {url}");
                await _connection.StartAsync(linked.Token);
                SetState(ConnectionState.Connected);
                YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: connected");
            }
            catch (Exception ex)
            {
                SetState(ConnectionState.Disconnected);
                YargLogger.LogError($"LobbyHubSession[#{_instanceId}]: connect failed — {ex.Message}");
                throw;
            }
            finally
            {
                lock (_lock) _inflightConnect = null;
            }
        }

        public async UniTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposing, 1) != 0) return;

            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: disposing");

            Application.quitting -= OnApplicationQuitting;

            // Fire explicit LeaveResults + LeaveLobby RPCs BEFORE cancelling
            // the lifetime token so the server's lobby state cleans up
            // promptly when the player closes the client window. The
            // server's OnDisconnectedAsync also runs LeaveAsync on socket
            // teardown, but that's reactive — relying on it leaves a
            // window where: (a) the SignalR Stop hasn't completed before
            // the process exits and the server has to wait for the TCP
            // keepalive to time out, and (b) LeaveAsync alone doesn't fire
            // LeaveResults, so a stale IsBackInLobby flag blocks the
            // remaining members' Start gate. Bound the await with a short
            // timeout — DisposeAsync may run from Application.quitting
            // where Unity gives us only a few hundred ms before forcibly
            // exiting; we'd rather skip the RPC than block the quit.
            try
            {
                using var leaveCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                leaveCts.CancelAfter(TimeSpan.FromMilliseconds(750));
                if (_orchestrator != null)
                {
                    // We're mid-game — flag back-in-lobby so the host's
                    // Start gate unblocks and the all-back transition can
                    // run if every other player also bailed.
                    await LeaveResultsAsync(leaveCts.Token);
                }
                await LeaveLobbyAsync(leaveCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected if the 750ms window expired or the connection
                // is already torn down — the server's OnDisconnectedAsync
                // will still clean up via the socket teardown path.
            }
            catch (Exception ex)
            {
                // Best-effort signal — server-side disconnect handler is
                // the backstop. Don't let a stray RPC failure here keep
                // the dispose from making progress.
                YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: leave-on-dispose threw — {ex.Message}");
            }

            // Cancel first so any pending main-thread continuations (SignalR
            // handlers + Track bodies) throw OperationCanceledException on resume
            // and exit without touching session state.
            try { _lifetimeCts.Cancel(); } catch { }

            var orch = _orchestrator;
            _orchestrator = null;
            if (orch != null)
            {
                orch.Ended -= OnOrchestratorEnded;
                try { await orch.DisposeAsync(); }
                catch (Exception ex) { YargLogger.LogException(ex); }
            }

            // Drain any in-flight ConnectAsync so we don't race the connection-build.
            UniTask? inflight;
            lock (_lock) inflight = _inflightConnect;
            if (inflight.HasValue)
            {
                try { await inflight.Value.SuppressCancellationThrow(); }
                catch (Exception ex) { YargLogger.LogException(ex); }
            }

            HubConnection conn;
            lock (_lock) { conn = _connection; _connection = null; }
            if (conn != null)
            {
                // StopAsync / DisposeAsync drain SignalR's async handler tasks
                // before returning — they observe the cancelled token and exit.
                try { await conn.StopAsync(); }
                catch (Exception ex) { YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: stop — {ex.Message}"); }
                try { await conn.DisposeAsync(); }
                catch (Exception ex) { YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: dispose — {ex.Message}"); }
            }

            // Drain fire-and-forget Track bodies (the lifecycle / SetState paths).
            // After cancel, each pending SwitchToMainThread continuation throws on
            // its next Update tick and the finally in Track decrements the counter.
            await UniTask.WaitUntil(() => Volatile.Read(ref _inflightHandlers) == 0);

            lock (_lock) { _byId.Clear(); _lastSequence = -1; }
            _currentLobby = null;
            _state = ConnectionState.Disconnected;
            _lifetimeCts.Dispose();

            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: disposed");
        }

        // ---------- Main-thread dispatch helpers ----------

        // Tracks fire-and-forget bodies that hop to the main thread so DisposeAsync
        // can await them. SignalR async handlers don't need this — SignalR's own
        // pipeline awaits them, and StopAsync/DisposeAsync drain them naturally.
        private async UniTaskVoid Track(Func<UniTask> body)
        {
            Interlocked.Increment(ref _inflightHandlers);
            try
            {
                await body();
            }
            catch (OperationCanceledException)
            {
                // Expected when _lifetimeCts is cancelled mid-dispatch.
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }
            finally
            {
                Interlocked.Decrement(ref _inflightHandlers);
            }
        }

        // ---------- Lobby RPCs ----------

        /// <summary>
        /// Invoke <c>ILobbyHub.CreateLobby</c>. On success, populates
        /// <see cref="CurrentLobby"/> from the result and fires
        /// <see cref="CurrentLobbyChanged"/>. Throws on hub error.
        /// </summary>
        public async UniTask<CreateLobbyResult> CreateLobbyAsync(
            CreateLobbyArgs args, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: CreateLobby name='{args.Name}', mode={args.GameMode}");
            var result = await conn.InvokeAsync<CreateLobbyResult>(
                nameof(ILobbyHub.CreateLobby), args, ct);
            // Continuation may resume off-main-thread depending on SyncContext.
            // Force back to main thread before touching _currentLobby.
            await UniTask.SwitchToMainThread();
            if (Volatile.Read(ref _disposing) != 0) return result;
            _currentLobby = LobbyRoomState.FromCreate(result.Lobby);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: CreateLobby ok — id={result.Lobby.Id}");
            CurrentLobbyChanged?.Invoke();
            return result;
        }

        /// <summary>
        /// Invoke <c>ILobbyHub.EnterLobby</c>. On success, populates
        /// <see cref="CurrentLobby"/> from the full result payload and fires
        /// <see cref="CurrentLobbyChanged"/>. Throws on hub error.
        /// </summary>
        public async UniTask<EnterLobbyResult> EnterLobbyAsync(
            EnterLobbyArgs args, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: EnterLobby id={args.LobbyId}");
            var result = await conn.InvokeAsync<EnterLobbyResult>(
                nameof(ILobbyHub.EnterLobby), args, ct);
            await UniTask.SwitchToMainThread();
            if (Volatile.Read(ref _disposing) != 0) return result;
            _currentLobby = LobbyRoomState.FromEnter(result);
            YargLogger.LogInfo(
                $"LobbyHubSession[#{_instanceId}]: EnterLobby ok — id={result.Lobby.Id}, "
                + $"members={_currentLobby.Members.Count}, "
                + $"lobbyLibrary={_currentLobby.LobbySongLibrary.Count}");
            CurrentLobbyChanged?.Invoke();
            return result;
        }

        /// <summary>
        /// Invoke <c>ILobbyHub.LeaveLobby</c> and clear <see cref="CurrentLobby"/>.
        /// Local state is always cleared, even if the server call throws — once
        /// the user backs out, they're out client-side.
        /// </summary>
        public async UniTask LeaveLobbyAsync(CancellationToken ct = default)
        {
            var conn = _connection;
            try
            {
                if (conn != null && _currentLobby != null)
                {
                    YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: LeaveLobby id={_currentLobby.LobbyId}");
                    await conn.InvokeAsync(nameof(ILobbyHub.LeaveLobby), ct);
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogWarning($"LobbyHubSession[#{_instanceId}]: LeaveLobby threw — {ex.Message}");
            }
            finally
            {
                await UniTask.SwitchToMainThread();
                if (Volatile.Read(ref _disposing) == 0 && _currentLobby != null)
                {
                    _currentLobby = null;
                    CurrentLobbyChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// Invoke <c>ILobbyHub.QueueSong</c>. The server broadcasts
        /// <c>OnSongQueued</c> back to all members (including the caller), which
        /// appends to <see cref="CurrentLobby"/>.<c>SongQueue</c> — so we don't
        /// pre-emptively mutate state here. Throws on hub error.
        /// </summary>
        public async UniTask<QueuedSongDto> QueueSongAsync(
            HashWrapper hash, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new QueueSongArgs(hash.ToString());
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: QueueSong hash={args.SongHash}");
            var result = await conn.InvokeAsync<QueuedSongDto>(
                nameof(ILobbyHub.QueueSong), args, ct);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: QueueSong ok — sequence={result.Sequence}");
            return result;
        }

        /// <summary>
        /// Invoke <c>ILobbyHub.RemoveQueuedSong</c>. The server broadcasts
        /// <c>OnSongDequeued</c> back to all members; we don't pre-emptively
        /// mutate <see cref="CurrentLobby"/>.<c>SongQueue</c>. Throws on hub error
        /// (e.g. caller isn't host, or sequence isn't in the queue).
        /// </summary>
        public async UniTask RemoveQueuedSongAsync(long sequence, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new RemoveQueuedSongArgs(sequence);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: RemoveQueuedSong sequence={sequence}");
            await conn.InvokeAsync(nameof(ILobbyHub.RemoveQueuedSong), args, ct);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: RemoveQueuedSong ok — sequence={sequence}");
        }

        /// <summary>
        /// Invoke <c>ILobbyHub.SendChatMessage</c>. The server broadcasts
        /// <c>OnChatMessage</c> back to all members including the caller, which
        /// appends to <see cref="CurrentLobby"/>.<c>ChatHistory</c> — so we don't
        /// pre-emptively mutate state here. Throws on hub error (validation_failed
        /// if text is empty or > 256 chars after trim).
        /// </summary>
        public async UniTask SendChatMessageAsync(string text, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new SendChatMessageArgs(text);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: SendChatMessage length={text?.Length ?? 0}");
            await conn.InvokeAsync(nameof(ILobbyHub.SendChatMessage), args, ct);
        }

        /// <summary>
        /// Invoke <c>ILobbyHub.StartGame</c>. The server validates the caller is
        /// the host, mints per-member game JWTs, and broadcasts <c>OnGameStarted</c>
        /// + <c>OnLobbyStatusChanged(GameStarted)</c> to every member. We don't
        /// mutate state here — the callback paths populate <see cref="CurrentLobby"/>
        /// and fire <see cref="GameStarted"/>.
        /// </summary>
        public async UniTask StartGameAsync(CancellationToken ct = default)
        {
            var conn = RequireConnection();
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: StartGame");
            await conn.InvokeAsync(nameof(ILobbyHub.StartGame), ct);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: StartGame ok — awaiting OnGameStarted");
        }

        /// <summary>
        /// Signal to the lobby that this player has closed the post-game
        /// results screen and is back at the song-select view. Called by
        /// <c>ScoreScreenMenu</c> on the Continue button when an online
        /// lobby is active. The host's Start button is gated on every
        /// member having reported in. Safe to call when no lobby is
        /// active — the server treats it as a no-op.
        /// </summary>
        public async UniTask LeaveResultsAsync(CancellationToken ct = default)
        {
            var conn = _connection;
            if (conn == null || _state != ConnectionState.Connected)
            {
                // Offline play / disconnected — nothing to signal. Don't throw.
                return;
            }
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: LeaveResults");
            await conn.InvokeAsync(nameof(ILobbyHub.LeaveResults), ct);
        }

        /// <summary>
        /// Bail out of the in-flight game session entirely (used by the
        /// in-gameplay Quit button). Tears down the UDP game session — the
        /// game server detects the disconnect and broadcasts
        /// <c>RemotePeerLeftPacket</c> to remaining peers, who hide the
        /// leaver's track. Also fires <see cref="LeaveResults"/> against
        /// the lobby hub so the leaver counts as back-in-lobby for the
        /// host's Start gate. Safe to call when no game is active.
        /// </summary>
        public void LeaveCurrentGame()
        {
            // Fire-and-forget the lobby signal so the host's Start gate
            // unblocks promptly. The hub-side RPC tolerates "no lobby"
            // gracefully so this is safe even after a partial disconnect.
            LeaveResultsAsync().Forget();

            // Disposing the orchestrator cascades into GameClientSession
            // disposal, which closes the UDP connection. The server's
            // OnPeerDisconnected then broadcasts RemotePeerLeft to remaining
            // peers. LobbyHubSession.OnOrchestratorEnded normally drives
            // this on Disconnected/GameEnded events — calling it directly
            // here just collapses the wait.
            var orch = _orchestrator;
            if (orch == null) return;
            _orchestrator = null;
            orch.Ended -= OnOrchestratorEnded;
            orch.DisposeAsync().Forget();
        }

        private HubConnection RequireConnection()
        {
            var conn = _connection;
            if (conn == null || _state != ConnectionState.Connected)
            {
                throw new InvalidOperationException(
                    "LobbyHubSession is not connected — call ConnectAsync first.");
            }
            return conn;
        }

        // ---------- In-lobby callbacks ----------
        //
        // SignalR invokes these on thread-pool threads. Each handler is an
        // async Task that switches to the main thread before touching
        // _currentLobby — SignalR awaits the returned task, preserving FIFO
        // message ordering on the receive pipeline. On dispose, _lifetimeCts
        // is cancelled and the SwitchToMainThread continuation throws
        // OperationCanceledException, so the handler exits without touching
        // session state.

        private async Task OnPlayerJoinedAsync(PlayerJoinedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            if (!_currentLobby.Members.Contains(e.UserId)) _currentLobby.Members.Add(e.UserId);
            _currentLobby.MemberNames[e.UserId] = e.DisplayName;
            _currentLobby.MemberInstruments[e.UserId] = (YARG.Core.Instrument) e.Instrument;
            // Joiners always land on the lobby/song-select screen — the
            // matching authoritative flag from the server is also true.
            _currentLobby.MemberIsBackInLobby[e.UserId] = true;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnPlayerJoined {e.UserId} ({e.DisplayName}) instrument={(YARG.Core.Instrument) e.Instrument}");
            CurrentLobbyChanged?.Invoke();

            if (e.UserId != _tokenProvider.UserId)
            {
                ToastManager.ToastInformation($"{e.DisplayName} joined the lobby");
            }
        }

        private async Task OnPlayerLeftAsync(PlayerLeftEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            // Resolve display name before removal so the toast can name the player.
            string displayName = _currentLobby.GetDisplayName(e.UserId);
            _currentLobby.Members.Remove(e.UserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnPlayerLeft {e.UserId}");
            CurrentLobbyChanged?.Invoke();

            if (e.UserId != _tokenProvider.UserId)
            {
                ToastManager.ToastInformation($"{displayName} left the lobby");
            }
        }

        private async Task OnLobbyClosedAsync(LobbyClosedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnLobbyClosed reason='{e.Reason}'");
            _currentLobby = null;
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnHostChangedAsync(HostChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.HostUserId = e.NewHostUserId;
            _currentLobby.HostName   = e.NewHostName;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnHostChanged -> {e.NewHostUserId} ({e.NewHostName})");
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnPlayerKickedAsync(PlayerKickedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            bool kickedSelf = e.UserId == _tokenProvider.UserId;
            // Resolve display name before removal for the non-self toast.
            string displayName = _currentLobby.GetDisplayName(e.UserId);
            _currentLobby.Members.Remove(e.UserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnPlayerKicked {e.UserId} reason='{e.Reason}'");
            if (kickedSelf)
            {
                _currentLobby = null;
                CurrentLobbyChanged?.Invoke();
                KickedFromLobby?.Invoke(e.Reason);
            }
            else
            {
                CurrentLobbyChanged?.Invoke();
                ToastManager.ToastInformation($"{displayName} was kicked from the lobby");
            }
        }

        private async Task OnLobbyStatusChangedAsync(LobbyStatusChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            var previous = _currentLobby.Status;
            _currentLobby.Status = e.Status;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnLobbyStatusChanged -> {e.Status}");
            CurrentLobbyChanged?.Invoke();

            // Surface the two new transitions the StartGame flow can produce
            // to non-host members. The host already sees these via the
            // LoadingContext that wraps StartGameAsync.
            bool isHost = _currentLobby.IsLocalHost;
            if (!isHost)
            {
                if (e.Status == LobbyStatus.Starting && previous != LobbyStatus.Starting)
                {
                    ToastManager.ToastInformation("Host is starting the game…");
                }
                else if (e.Status == LobbyStatus.SongSelect && previous == LobbyStatus.Starting)
                {
                    // Server rolled the lobby back from Starting → SongSelect.
                    // The only path that produces this transition is an
                    // allocator failure during StartGame (hub throws
                    // "allocation_failed" to the host and broadcasts the
                    // rollback to everyone).
                    ToastManager.ToastWarning("Game start failed. No servers available.");
                }
            }
        }

        private async Task OnLobbySongLibraryUpdatedAsync(LobbySongLibraryUpdatedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;

            // Mutate the lobby state set directly. While the picker is open,
            // MusicLibraryMenu.AllowedSongHashes is reference-aliased to this
            // same HashSet (seed happens in LobbyViewMenu.OnQueueSongClicked),
            // so live additions/removals are visible without a refresh pass.
            // Skip hashes the local player doesn't own — they can't appear in the
            // intersected view either way, and counting them would inflate the toast.
            var library = _currentLobby.LobbySongLibrary;
            int removedCount = 0;
            int addedCount = 0;
            if (e.Removed != null)
            {
                foreach (var h in e.Removed)
                {
                    if (library.Remove(ToHashWrapper(h)))
                        removedCount++;
                }
            }
            if (e.Added != null)
            {
                foreach (var h in e.Added)
                {
                    var hw = ToHashWrapper(h);
                    if (SongContainer.SongsByHash.ContainsKey(hw) && library.Add(hw))
                        addedCount++;
                }
            }

            CurrentLobbyChanged?.Invoke();
            MusicLibraryMenu.NotifyAllowedSongsChanged();

            if (addedCount > 0 && removedCount > 0)
            {
                int total = addedCount + removedCount;
                ToastManager.ToastInformation(
                    $"Lobby library: +{addedCount} / -{removedCount} song{(total == 1 ? "" : "s")}");
            }
            else if (addedCount > 0)
            {
                ToastManager.ToastInformation(
                    $"{addedCount} song{(addedCount == 1 ? "" : "s")} added to lobby library");
            }
            else if (removedCount > 0)
            {
                ToastManager.ToastInformation(
                    $"{removedCount} song{(removedCount == 1 ? "" : "s")} removed from lobby library");
            }
        }

        private async Task OnSongQueuedAsync(SongQueuedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.SongQueue.Add(e.Song);
            CurrentLobbyChanged?.Invoke();

            if (e.Song.RequesterId != _tokenProvider.UserId)
            {
                var hash = HashWrapper.FromString(e.Song.SongHash);
                string songLabel = SongContainer.SongsByHash.TryGetValue(hash, out var songs)
                    ? songs[0].Name
                    : e.Song.SongHash;
                ToastManager.ToastInformation($"{songLabel} was queued");
            }
        }

        private async Task OnSongRemovedFromQueueAsync(SongRemovedFromQueueEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            // Resolve song label before removal so the toast can name it.
            int idx = _currentLobby.SongQueue.FindIndex(q => q.Sequence == e.Sequence);
            string songLabel = null;
            if (idx >= 0)
            {
                var hash = HashWrapper.FromString(_currentLobby.SongQueue[idx].SongHash);
                songLabel = SongContainer.SongsByHash.TryGetValue(hash, out var songs)
                    ? songs[0].Name
                    : _currentLobby.SongQueue[idx].SongHash;
            }
            _currentLobby.SongQueue.RemoveAll(q => q.Sequence == e.Sequence);
            CurrentLobbyChanged?.Invoke();

            if (songLabel == null) return;
            switch (e.Reason)
            {
                case SongRemovalReason.Played:
                    // Score screen already communicates this — no toast.
                    break;
                case SongRemovalReason.RequesterLeft:
                    ToastManager.ToastInformation($"{songLabel} was removed (requester left)");
                    break;
                case SongRemovalReason.Removed:
                default:
                    ToastManager.ToastInformation($"{_currentLobby.HostName} removed {songLabel} from queue");
                    break;
            }
        }

        private async Task OnQueuedSongAvailabilityChangedAsync(QueuedSongAvailabilityChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            int idx = _currentLobby.SongQueue.FindIndex(q => q.Sequence == e.Sequence);
            if (idx < 0) return;
            var existing = _currentLobby.SongQueue[idx];
            var missing = new HashSet<string>(existing.MissingFor ?? Array.Empty<string>());
            if (e.RemovedMissing != null) foreach (var u in e.RemovedMissing) missing.Remove(u);
            if (e.AddedMissing   != null) foreach (var u in e.AddedMissing)   missing.Add(u);
            var newMissing = new string[missing.Count];
            missing.CopyTo(newMissing);
            _currentLobby.SongQueue[idx] = existing with { MissingFor = newMissing };
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnChatMessageAsync(ChatMessageEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.ChatHistory.Add(e.Message);
            CurrentLobbyChanged?.Invoke();
        }

        private async Task OnGameStartedAsync(GameStartedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.Status             = LobbyStatus.GameStarted;
            _currentLobby.GameServerEndpoint = e.GameServerEndpoint;
            _currentLobby.GameToken          = e.GameToken;
            _currentLobby.GameTokenExpiresAt = e.ExpiresAt;
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: OnGameStarted endpoint={e.GameServerEndpoint}");
            CurrentLobbyChanged?.Invoke();

            StartOrchestratorAsync().Forget();
        }

        private async Task OnPlayerLobbyReadyChangedAsync(PlayerLobbyReadyChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            _currentLobby.MemberIsBackInLobby[e.UserId] = e.IsBackInLobby;
            YargLogger.LogFormatInfo(
                "LobbyHubSession[#{0}]: OnPlayerLobbyReadyChanged userId={1} isBackInLobby={2}",
                _instanceId, e.UserId, e.IsBackInLobby);
            CurrentLobbyChanged?.Invoke();
        }

        // ---------- helpers ----------

        // Convert a wire-format hash string to the HashWrapper used by SongContainer indices.
        // Mirrors the pattern at OnSongQueuedAsync / OnSongFinalizedAsync.
        private static HashWrapper ToHashWrapper(string s) => HashWrapper.FromString(s.AsSpan());

        // Note: the MusicLibraryMenu lobby filter (AllowedSongHashes) is no
        // longer seeded here. LobbyViewMenu.OnQueueSongClicked owns the seed
        // (re-applied every picker open) and MusicLibraryMenu.OnDisable owns
        // the cleanup. The HashSet stored in _currentLobby.LobbySongLibrary is
        // still mutated in-place by OnLobbySongLibraryUpdatedAsync, so live
        // updates flow once the picker is open and aliased to it.

        private async UniTaskVoid StartOrchestratorAsync()
        {
            if (_orchestrator != null)
            {
                YargLogger.LogWarning(
                    $"LobbyHubSession[#{_instanceId}]: game flow already in progress; ignoring OnGameStarted");
                return;
            }

            LobbyGameOrchestrator orch;
            try
            {
                orch = await LobbyGameOrchestrator.InitializeAsync(this);
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
                return;
            }

            if (Volatile.Read(ref _disposing) != 0)
            {
                // We were torn down while constructing — dispose what we just built.
                try { await orch.DisposeAsync(); }
                catch (Exception disposeEx) { YargLogger.LogException(disposeEx); }
                return;
            }

            _orchestrator = orch;
            _orchestrator.Ended += OnOrchestratorEnded;
        }

        private void OnOrchestratorEnded() => EndOrchestratorAsync().Forget();

        private async UniTaskVoid EndOrchestratorAsync()
        {
            var orch = _orchestrator;
            if (orch == null) return;
            _orchestrator = null;
            orch.Ended -= OnOrchestratorEnded;
            try { await orch.DisposeAsync(); }
            catch (Exception ex) { YargLogger.LogException(ex); }
        }

        private bool IsForCurrentLobby(string lobbyId)
        {
            // Called only on main thread from inside dispatcher actions.
            return _currentLobby != null && _currentLobby.LobbyId == lobbyId;
        }

        // ---------- Browse-scope handlers ----------

        private async Task ApplySnapshotAsync(LobbyDto[] lobbies)
        {
            lock (_lock)
            {
                _byId.Clear();
                _lastSequence = -1;
                if (lobbies != null)
                {
                    foreach (var dto in lobbies)
                    {
                        if (dto?.Id != null) _byId[dto.Id] = dto;
                    }
                }
            }
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: snapshot — {lobbies?.Length ?? 0} lobbies");
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            LobbiesChanged?.Invoke();
        }

        private async Task ApplyBatchAsync(LobbyBatchUpdate batch)
        {
            if (batch == null) return;

            lock (_lock)
            {
                if (batch.Sequence <= _lastSequence) return;

                if (batch.Added != null)
                {
                    foreach (var dto in batch.Added)
                        if (dto?.Id != null) _byId[dto.Id] = dto;
                }
                if (batch.Updated != null)
                {
                    foreach (var dto in batch.Updated)
                        if (dto?.Id != null) _byId[dto.Id] = dto;
                }
                if (batch.Removed != null)
                {
                    foreach (var id in batch.Removed)
                        if (id != null) _byId.Remove(id);
                }

                _lastSequence = batch.Sequence;
            }
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            LobbiesChanged?.Invoke();
        }

        private void SetState(ConnectionState newState)
        {
            bool changed;
            lock (_lock)
            {
                changed = _state != newState;
                _state = newState;
            }
            if (changed)
            {
                YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: state -> {newState}");
                Track(async () =>
                {
                    await UniTask.SwitchToMainThread(_lifetimeCts.Token);
                    StateChanged?.Invoke(newState);
                }).Forget();
            }
        }
    }
}
