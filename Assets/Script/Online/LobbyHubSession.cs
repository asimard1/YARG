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
using YARG.Localization;
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
        // In-flight orchestrator disposal — StartOrchestratorAsync awaits this
        // so a new song's NetManager doesn't spin up while the prior one is
        // still tearing down. Null when no disposal is pending. Guarded by _lock.
        private UniTask? _pendingOrchestratorDispose;

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
        /// Invoke <c>ILobbyHub.TransferHost</c>. Host-only on the server side; the
        /// hub broadcasts <c>OnHostChanged</c> to every member, which the session's
        /// callback handler folds into <see cref="CurrentLobby"/> and surfaces via
        /// <see cref="CurrentLobbyChanged"/>. No local pre-emptive mutation.
        /// </summary>
        public async UniTask TransferHostAsync(string targetUserId, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new TransferHostArgs(targetUserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: TransferHost target={targetUserId}");
            await conn.InvokeAsync(nameof(ILobbyHub.TransferHost), args, ct);
        }

        /// <summary>
        /// Invoke <c>ILobbyHub.KickPlayer</c>. Host-only; the hub broadcasts
        /// <c>OnPlayerKicked</c> to every member (including the kickee, who tears
        /// down their session). The session callback removes the user from
        /// <see cref="CurrentLobby"/>.<c>Members</c>.
        /// </summary>
        public async UniTask KickPlayerAsync(string targetUserId, CancellationToken ct = default)
        {
            var conn = RequireConnection();
            var args = new KickPlayerArgs(targetUserId);
            YargLogger.LogInfo($"LobbyHubSession[#{_instanceId}]: KickPlayer target={targetUserId}");
            await conn.InvokeAsync(nameof(ILobbyHub.KickPlayer), args, ct);
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
        /// Push a freshly-scanned local library to the lobby so the server can
        /// recompute the shared-library intersection. Called by the picker's
        /// orange → ScanSongs flow after <c>SongContainer.RunRefresh</c>
        /// completes. The server broadcasts the resulting delta via
        /// <c>OnLobbySongLibraryUpdated</c>, which the existing handler folds
        /// into <see cref="LobbyRoomState.LobbySongLibrary"/>. Safe to call
        /// when no lobby is active — silently no-ops.
        /// </summary>
        public async UniTask UpdateLibraryAsync(SongLibraryDto library, CancellationToken ct = default)
        {
            var conn = _connection;
            if (conn == null || _state != ConnectionState.Connected || _currentLobby == null)
            {
                return;
            }
            YargLogger.LogInfo(
                $"LobbyHubSession[#{_instanceId}]: UpdateLibrary — hashes={library?.SongHashes?.Length ?? 0}");
            await conn.InvokeAsync(nameof(ILobbyHub.UpdateLibrary), new UpdateLibraryArgs(library), ct);
        }

        /// <summary>
        /// Bail out of the in-flight game session entirely. Tears down the UDP
        /// game session — the game server detects the disconnect and broadcasts
        /// <c>RemotePeerLeftPacket</c> to remaining peers, who hide the leaver's
        /// track. Also fires <see cref="LeaveResults"/> against the lobby hub so
        /// the leaver counts as back-in-lobby for the host's Start gate. Safe to
        /// call when no game is active.
        ///
        /// Used by:
        ///   - The in-gameplay Quit button (player leaves mid-song).
        ///   - <c>ScoreScreenMenu.Continue</c> when transitioning back to the
        ///     lobby after a normal song completion. Disposing the orchestrator
        ///     at that point closes the UDP connection, so when all peers have
        ///     reached Continue the game-side <c>GameSession</c> winds down via
        ///     the last-peer-disconnect path (which cancels the straggler CTS)
        ///     instead of waiting 30 s for the straggler timer to fire.
        ///   - <see cref="GameManager.OnOnlineSessionEnded"/> abnormal-bail path.
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
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.PlayerJoined", e.DisplayName));
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
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.PlayerLeft", displayName));
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
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.PlayerKicked", displayName));
            }
        }

        private async Task OnLobbyStatusChangedAsync(LobbyStatusChangedEvent e)
        {
            try { await UniTask.SwitchToMainThread(_lifetimeCts.Token); }
            catch (OperationCanceledException) { return; }
            if (!IsForCurrentLobby(e.LobbyId)) return;
            var previous = _currentLobby.Status;
            _currentLobby.Status = e.Status;
            // Drive the in-progress flag off the actual status transitions: arming
            // it on entry into GameStarted, clearing it when the lobby winds back
            // to SongSelect. The Played-removal handler owns the middle transition
            // (song over → members are on results, not in-game) so the flag may
            // already be false by the time SongSelect arrives.
            //
            // The GameStarted branch ALSO mirrors a per-member IsBackInLobby flip
            // the server makes silently: ConfirmStartGameAsync flips every
            // member's flag to false atomically with the GameStarted transition,
            // but the hub only emits OnLobbyStatusChanged for that, not
            // per-member OnPlayerLobbyReadyChanged events. Without mirroring
            // here the client's MemberIsBackInLobby dict stays stuck at "true"
            // for the entire in-game + results window, the stage always resolves
            // to InLobby, and the row never tints away from white.
            //
            // SongSelect is NOT symmetric: FinishGameAsync flips the status but
            // does NOT touch IsBackInLobby — members stay false because they're
            // now on the results screen, and each member's flag flips to true
            // individually via LeaveResults' OnPlayerLobbyReadyChanged broadcast
            // when they hit Continue. Don't bulk-set true on SongSelect or the
            // results-screen rows go white the instant the song ends.
            if (e.Status == LobbyStatus.GameStarted)
            {
                _currentLobby.IsSongInProgress = true;
                foreach (var uid in _currentLobby.Members)
                {
                    _currentLobby.MemberIsBackInLobby[uid] = false;
                }
            }
            else if (e.Status == LobbyStatus.SongSelect)
            {
                _currentLobby.IsSongInProgress = false;
            }
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
                    LobbyChatterToast(Localize.Key("Menu.Online.Toast.HostStartingGame"));
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

            // If the picker is open with an active filter, re-derive it from the
            // freshly-updated lobby library so newly-shared songs become queueable
            // immediately (and newly-unshared songs drop out). Without this the
            // picker's filter stays frozen at the snapshot it captured at open
            // time, and the user has to close/reopen to see scan results. Skip
            // when no filter is active (offline picker / picker not yet seeded).
            if (MusicLibraryMenu.AllowedSongHashes != null)
            {
                var rebuilt = YARG.Menu.Online.LobbyViewMenu.BuildPlayableSongSet(_currentLobby);
                if (rebuilt != null)
                {
                    MusicLibraryMenu.AllowedSongHashes = rebuilt;
                }
            }
            MusicLibraryMenu.NotifyAllowedSongsChanged();

            if (addedCount > 0 && removedCount > 0)
            {
                int total = addedCount + removedCount;
                LobbyChatterToast(Localize.KeyFormat(
                    total == 1
                        ? "Menu.Online.Toast.LibrarySongsBoth.Singular"
                        : "Menu.Online.Toast.LibrarySongsBoth.Plural",
                    addedCount, removedCount));
            }
            else if (addedCount > 0)
            {
                LobbyChatterToast(addedCount == 1
                    ? Localize.Key("Menu.Online.Toast.LibrarySongsAdded.Singular")
                    : Localize.KeyFormat("Menu.Online.Toast.LibrarySongsAdded.Plural", addedCount));
            }
            else if (removedCount > 0)
            {
                LobbyChatterToast(removedCount == 1
                    ? Localize.Key("Menu.Online.Toast.LibrarySongsRemoved.Singular")
                    : Localize.KeyFormat("Menu.Online.Toast.LibrarySongsRemoved.Plural", removedCount));
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
                // Pull the queuer's display name out of the member map. GetDisplayName
                // falls back to the userId string if we haven't seen the member yet,
                // which keeps the toast useful even for users who joined after our
                // session's MemberNames was last seeded.
                string requesterName = _currentLobby.GetDisplayName(e.Song.RequesterId);
                LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.SongQueued", requesterName, songLabel));
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

            // The chart finishing is the wire signal that flips members from "in-game"
            // to "on results screen" — see LobbyRoomState.IsSongInProgress. Set this
            // BEFORE the songLabel-null early-out so a removal for a song that's no
            // longer in our local queue (e.g. queue mutated under our feet) still
            // updates the stage derivation.
            if (e.Reason == SongRemovalReason.Played)
            {
                _currentLobby.IsSongInProgress = false;
            }

            if (songLabel == null) return;
            switch (e.Reason)
            {
                case SongRemovalReason.Played:
                    // Score screen already communicates this — no toast.
                    break;
                case SongRemovalReason.RequesterLeft:
                    LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.SongRemovedRequesterLeft", songLabel));
                    break;
                case SongRemovalReason.Removed:
                default:
                    // RemovedByUserId is populated by the server only on the
                    // explicit-Removed path. Falls back to HostName on legacy
                    // servers / unexpected reasons so we never show an empty name.
                    string removerName = !string.IsNullOrEmpty(e.RemovedByUserId)
                        ? _currentLobby.GetDisplayName(e.RemovedByUserId)
                        : _currentLobby.HostName;
                    LobbyChatterToast(Localize.KeyFormat("Menu.Online.Toast.SongRemovedByPlayer", removerName, songLabel));
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

        // Lobby chatter (joins, leaves, kicks, queue churn, library deltas) is useful
        // info in the menu/lobby views but pure noise stacked over the highway during
        // a song. Route those toasts through this helper so they auto-suppress while
        // the Gameplay scene is active. Score/Menu scenes still get them.
        // Warnings/errors keep their direct ToastManager calls — a "Game start failed"
        // or similar shouldn't be silenced.
        private static void LobbyChatterToast(string message)
        {
            if (GlobalVariables.Instance != null
                && GlobalVariables.Instance.CurrentScene == SceneIndex.Gameplay)
            {
                return;
            }
            ToastManager.ToastInformation(message);
        }

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

            // Wait for the previous orchestrator's dispose to actually finish.
            // OnOrchestratorEnded fires the dispose via .Forget(), so the new
            // OnGameStarted can land while the prior NetManager + GameClientSession
            // are still tearing down. Without this await, two LiteNetLib logic
            // threads briefly coexist, the prior one still holding a UDP socket
            // and event subscriptions — extra GC pressure exactly during the
            // window where the new NetManager needs to send its first pings.
            UniTask? pendingDispose;
            lock (_lock) pendingDispose = _pendingOrchestratorDispose;
            if (pendingDispose.HasValue)
            {
                try { await pendingDispose.Value.SuppressCancellationThrow(); }
                catch (Exception ex) { YargLogger.LogException(ex); }
            }

            // Run a full GC on the predictable boundary between songs. The Mono
            // collector is stop-the-world, so the pause it produces will freeze
            // the NetManager's logic thread; doing it here (before we connect
            // the new game session) lets the pause happen while no keep-alive
            // contract exists yet, instead of during the new song's load when
            // the server's DisconnectTimeout would fire on the silence.
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex) { YargLogger.LogException(ex); }

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

        private void OnOrchestratorEnded()
        {
            // Capture the dispose task so the next OnGameStarted can await it
            // (see StartOrchestratorAsync). Without this handoff, the next song
            // can start spinning up a fresh NetManager while the previous one's
            // logic thread is still draining, doubling GC pressure exactly when
            // the new connection needs to send its first keep-alive pings.
            //
            // .Preserve() makes the UniTask multi-await: the .Forget() here and
            // StartOrchestratorAsync's later await are two separate consumers.
            // We don't bother clearing _pendingOrchestratorDispose after it
            // completes — awaiting an already-finished preserved task is a
            // no-op, and the next OnOrchestratorEnded overwrites it anyway.
            var task = EndOrchestratorAsync().Preserve();
            lock (_lock) _pendingOrchestratorDispose = task;
            task.Forget();
        }

        private async UniTask EndOrchestratorAsync()
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
