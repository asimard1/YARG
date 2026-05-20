using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Online.Game.Contracts.Enums;
using YARG.Online.Game.Contracts.Packets;

namespace YARG.Online
{
    /// <summary>
    /// LiteNetLib UDP client for the in-game session. Lifetime is short — created by
    /// <see cref="LobbyGameOrchestrator"/> when the lobby hub broadcasts game start,
    /// and disposed by it on <see cref="GameEnded"/>/<see cref="Disconnected"/> or when
    /// the orchestrator itself is torn down. All public events are raised on the Unity
    /// main thread — each LiteNetLib callback hops via <see cref="UniTask.SwitchToMainThread()"/>
    /// inside a tracked fire-and-forget body so <see cref="DisposeAsync"/> can wait for
    /// in-flight dispatches to drain.
    /// </summary>
    public sealed class GameClientSession
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

        private readonly object _lock = new();
        private NetManager _manager;
        private EventBasedNetListener _listener;
        private NetPeer _serverPeer;
        private CancellationTokenSource _pollCts;
        private UniTask? _pollLoop;
        private TaskCompletionSource<bool> _connectOutcome;

        private readonly CancellationTokenSource _lifetimeCts = new();
        private int _disposed; // 0 = alive, 1 = disposing/disposed

        // Count of fire-and-forget Track() bodies currently in flight. DisposeAsync
        // waits for this to drain to zero before tearing down the NetManager, so a
        // callback racing with shutdown can't fire an event after disposal.
        private int _inflightHandlers;

        // Per-peer destination pipes for inbound EngineInputBatch records. Installed
        // by OnlineSessionDirector via AttachEngineInputSinks; the poll thread writes
        // GameInput-shaped records straight into the matching writer (no main-thread hop).
        // Volatile so a Dispose-time swap is visible immediately to the poll thread.
        private volatile IReadOnlyDictionary<int, PipeWriter> _engineInputSinks;

        public GameClientSession()
        {
            YargLogger.LogInfo("GameClientSession: created");
        }

        public bool IsConnected
        {
            get { lock (_lock) return _manager != null && _serverPeer != null; }
        }

        /// <summary>Fired on the Unity main thread when the server broadcasts
        /// <see cref="PacketOpcode.GameStart"/>. Payload contains every peer's loadout —
        /// enough to instantiate one engine per player.</summary>
        public event Action<PeerLoadout[]> GameStarted;

        /// <summary>Fired on the Unity main thread when the server broadcasts
        /// <see cref="PacketOpcode.GameStartCue"/>. Carries the wall-clock instant where
        /// each client should align its <c>songTime=0</c>.</summary>
        public event Action<long, int> StartCueReceived;

        /// <summary>Fired on the LiteNetLib poll thread (NOT the main thread) when a
        /// <see cref="PacketOpcode.Pong"/> arrives. Args: <c>clientTickMs</c> (echoed from
        /// the originating ping), <c>serverUtcMs</c> (server's wall-clock at reply),
        /// <c>receiveLocalUtcMs</c> (this client's wall-clock at packet arrival, sampled
        /// before deserialization). Stays on the poll thread because clock-sync wants the
        /// arrival timestamp as close to the wire as possible — a main-thread hop adds
        /// frame-time jitter to the very thing we're measuring.</summary>
        public event Action<long, long, long> PongReceived;

        /// <summary>Fired on the Unity main thread when a remote peer drops mid-session.
        /// Receivers should mark that <c>YargPlayer</c> DNF and stop expecting more inputs
        /// for that peer id.</summary>
        public event Action<int> RemotePeerLeft;

        /// <summary>Fired on the Unity main thread when the server broadcasts
        /// <see cref="PacketOpcode.GameEnd"/> (all peers complete or straggler-timed-out).</summary>
        public event Action GameEnded;

        /// <summary>Fired on the Unity main thread when the UDP connection drops for any
        /// reason (server stop, idle timeout, network error).</summary>
        public event Action Disconnected;

        /// <summary>Connect to the game server. Single-shot — throws if called on a
        /// session whose <see cref="NetManager"/> is already started.</summary>
        public async UniTask<bool> ConnectAsync(IPEndPoint endpoint, string connectionKey, string jwt, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_manager != null)
                {
                    throw new InvalidOperationException(
                        "GameClientSession already has an active NetManager — call DisposeAsync first.");
                }

                _listener = new EventBasedNetListener();
                _manager = new NetManager(_listener) { UnconnectedMessagesEnabled = false };
                _connectOutcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                _listener.PeerConnectedEvent += peer =>
                {
                    _serverPeer = peer;
                    YargLogger.LogInfo($"GameClientSession: connected — peerId={peer.Id}");
                    _connectOutcome.TrySetResult(true);
                };
                _listener.PeerDisconnectedEvent += (peer, info) =>
                {
                    YargLogger.LogInfo($"GameClientSession: disconnected — reason={info.Reason}");
                    _serverPeer = null;
                    _connectOutcome.TrySetResult(false);
                    Track(async () =>
                    {
                        await UniTask.SwitchToMainThread(_lifetimeCts.Token);
                        Disconnected?.Invoke();
                    }).Forget();
                };
                _listener.NetworkReceiveEvent += OnNetworkReceive;
                _listener.NetworkErrorEvent += (ep, err) =>
                    YargLogger.LogWarning($"GameClientSession: network error from {ep} — {err}");
            }

            if (!_manager.Start())
            {
                YargLogger.LogError("GameClientSession: NetManager failed to start.");
                await DisposeAsync();
                return false;
            }

            _pollCts = new CancellationTokenSource();
            _pollLoop = PollLoopAsync(_pollCts.Token);

            var writer = new NetDataWriter();
            writer.Put(connectionKey);
            writer.Put(jwt);
            _manager.Connect(endpoint, writer);

            YargLogger.LogInfo($"GameClientSession: connecting to {endpoint}…");

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
                timeoutCts.CancelAfter(ConnectTimeout);
                return await _connectOutcome.Task.AsUniTask().AttachExternalCancellation(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                YargLogger.LogWarning("GameClientSession: connect timed out.");
                await DisposeAsync();
                return false;
            }
        }

        public void SendLoadout(InstrumentId instrument, DifficultyId difficulty, Guid enginePreset)
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                throw new InvalidOperationException("GameClientSession not connected; cannot send loadout.");
            }

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.SetLoadout, new SetLoadoutPacket
            {
                Instrument = instrument,
                Difficulty = difficulty,
                EnginePreset = enginePreset,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            YargLogger.LogInfo($"GameClientSession: SendLoadout instrument={instrument} difficulty={difficulty}");
        }

        /// <summary>Send a batch of local-player engine inputs to the server for fan-out.
        /// PeerId on the wire is left at 0; the server stamps it with the sender's id before
        /// fanning out to other peers in the session. Safe to call from the Unity main thread.</summary>
        public void SendEngineInputs(IReadOnlyList<EngineInputRecord> batch)
        {
            if (batch == null || batch.Count == 0) return;
            var peer = _serverPeer;
            if (peer == null) return;

            var packet = new EngineInputBatchPacket
            {
                PeerId = 0,
                Inputs = ToArray(batch),
            };
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.EngineInputBatch, packet);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendPeerReady()
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                YargLogger.LogWarning("GameClientSession.SendPeerReady: no server peer; dropping.");
                return;
            }
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.PeerReady, new PeerReadyPacket());
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            YargLogger.LogInfo("GameClientSession: SendPeerReady");
        }

        /// <summary>Send a clock-sync probe. <paramref name="clientTickMs"/> is opaque to the
        /// server — it is echoed back verbatim so the caller can pair the reply with the
        /// request and measure RTT. Safe to call from any thread once connected.</summary>
        public void SendPing(long clientTickMs)
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                YargLogger.LogWarning("GameClientSession.SendPing: no server peer; dropping.");
                return;
            }
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.Ping, new PingPacket { ClientTickMs = clientTickMs });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Install (or, with <c>null</c>, remove) the per-peer destination pipes for
        /// inbound EngineInputBatch packets. Called by <see cref="OnlineSessionDirector"/>:
        /// once on game-start with the full map, and once on dispose with <c>null</c>.
        /// Producer writes (LiteNetLib poll thread) snapshot the volatile field once per
        /// packet, so a swap from the main thread races safely.
        /// </summary>
        public void AttachEngineInputSinks(IReadOnlyDictionary<int, PipeWriter> sinks)
        {
            _engineInputSinks = sinks;
        }

        public void SendGameComplete()
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                YargLogger.LogWarning("GameClientSession.SendGameComplete: no server peer; dropping.");
                return;
            }
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.GameComplete, new GameCompletePacket());
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            YargLogger.LogInfo("GameClientSession: SendGameComplete");
        }

        public async UniTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            YargLogger.LogInfo("GameClientSession: disposing");

            try { _lifetimeCts.Cancel(); } catch { }

            // Drain the poll loop so it doesn't tick a stopped manager.
            UniTask? poll;
            CancellationTokenSource pollCts;
            lock (_lock)
            {
                poll = _pollLoop;
                pollCts = _pollCts;
                _pollLoop = null;
                _pollCts = null;
            }
            if (pollCts != null)
            {
                try { pollCts.Cancel(); } catch { }
                pollCts.Dispose();
            }
            if (poll.HasValue)
            {
                try { await poll.Value.SuppressCancellationThrow(); }
                catch (Exception ex) { YargLogger.LogException(ex); }
            }

            // Drain in-flight Track bodies. After cancel, each pending
            // SwitchToMainThread continuation throws on its next Update tick and
            // the finally in Track decrements the counter.
            await UniTask.WaitUntil(() => Volatile.Read(ref _inflightHandlers) == 0);

            NetManager manager;
            lock (_lock)
            {
                manager = _manager;
                _manager = null;
                _listener = null;
                _serverPeer = null;
                _connectOutcome = null;
            }
            if (manager != null)
            {
                try { manager.Stop(); }
                catch (Exception ex) { YargLogger.LogWarning($"GameClientSession: stop — {ex.Message}"); }
            }

            _lifetimeCts.Dispose();

            YargLogger.LogInfo("GameClientSession: disposed");
        }

        // Tracks fire-and-forget bodies that hop to the main thread so DisposeAsync
        // can await them. OperationCanceledException from a cancelled SwitchToMainThread
        // is expected during shutdown and swallowed silently.
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

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            // Sample local UTC ms before doing anything else with the packet. The Pong case
            // uses this as the receive timestamp for clock-sync; sampling later (after the
            // opcode read or deserialize) would attribute a few hundred microseconds of
            // parsing work to network RTT.
            long receiveLocalUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                if (reader.AvailableBytes < 1) return;

                var opcode = GamePacketReader.ReadOpcode(reader);
                switch (opcode)
                {
                    case PacketOpcode.GameStart:
                    {
                        var startPacket = new GameStartPacket();
                        startPacket.Deserialize(reader);
                        var loadouts = startPacket.Loadouts;
                        YargLogger.LogInfo($"GameClientSession: GameStart received — loadouts={loadouts.Length}");
                        Track(async () =>
                        {
                            await UniTask.SwitchToMainThread(_lifetimeCts.Token);
                            GameStarted?.Invoke(loadouts);
                        }).Forget();
                        break;
                    }
                    case PacketOpcode.GameStartCue:
                    {
                        var cuePacket = new GameStartCuePacket();
                        cuePacket.Deserialize(reader);
                        var originUtcMs = cuePacket.SongOriginUtcMs;
                        var countdownMs = cuePacket.CountdownMs;
                        YargLogger.LogInfo($"GameClientSession: GameStartCue received — origin={originUtcMs}");
                        Track(async () =>
                        {
                            await UniTask.SwitchToMainThread(_lifetimeCts.Token);
                            StartCueReceived?.Invoke(originUtcMs, countdownMs);
                        }).Forget();
                        break;
                    }
                    case PacketOpcode.EngineInputBatch:
                    {
                        // SPSC fast path: stays on the poll thread, no SwitchToMainThread,
                        // no managed-array allocation. Producer parses the (peerId, count,
                        // records) prefix manually and writes count GameInputs into the
                        // matching per-peer PipeWriter; BasePlayer's UpdateInputs drains
                        // the matching reader on the main thread.
                        int sourcePeerId = reader.GetInt();
                        int count = reader.GetInt();
                        int recordSize = Unsafe.SizeOf<GameInput>(); // 16 bytes (Explicit, Pack=1)
                        int bytes = count * recordSize;
                        if (count < 0 || reader.AvailableBytes < bytes)
                        {
                            YargLogger.LogWarning(
                                $"GameClientSession: malformed EngineInputBatch — count={count}, " +
                                $"available={reader.AvailableBytes}; dropping packet.");
                            break;
                        }

                        var sinks = _engineInputSinks;
                        PipeWriter writer = null;
                        if (sinks != null) sinks.TryGetValue(sourcePeerId, out writer);

                        if (writer != null && count > 0)
                        {
                            try
                            {
                                var span = writer.GetSpan(bytes);
                                var inputs = MemoryMarshal.Cast<byte, GameInput>(span);
                                for (int i = 0; i < count; i++)
                                {
                                    inputs[i] = new GameInput(
                                        reader.GetDouble(),
                                        reader.GetInt(),
                                        reader.GetInt());
                                }
                                writer.Advance(bytes);
                                // Fire-and-forget: backpressure is disabled, so this never
                                // actually awaits — the ValueTask completes synchronously.
                                _ = writer.FlushAsync();
                            }
                            catch (InvalidOperationException ex)
                            {
                                // Writer was completed by Dispose on the main thread
                                // between the volatile snapshot and the write. Expected
                                // during teardown.
                                YargLogger.LogWarning(
                                    $"GameClientSession: EngineInputBatch write into completed pipe " +
                                    $"for peerId={sourcePeerId} — {ex.Message}");
                            }
                        }
                        else
                        {
                            // No sink registered for this peer (or no sinks at all yet).
                            // Skip past the records so the reader stays aligned with the
                            // outer dispatch loop's AvailableBytes check.
                            for (int i = 0; i < count; i++)
                            {
                                reader.GetDouble();
                                reader.GetInt();
                                reader.GetInt();
                            }
                        }
                        break;
                    }
                    case PacketOpcode.RemotePeerLeft:
                    {
                        var leftPacket = new RemotePeerLeftPacket();
                        leftPacket.Deserialize(reader);
                        var leftPeerId = leftPacket.PeerId;
                        YargLogger.LogInfo($"GameClientSession: RemotePeerLeft peerId={leftPeerId}");
                        Track(async () =>
                        {
                            await UniTask.SwitchToMainThread(_lifetimeCts.Token);
                            RemotePeerLeft?.Invoke(leftPeerId);
                        }).Forget();
                        break;
                    }
                    case PacketOpcode.GameEnd:
                    {
                        var endPacket = new GameEndPacket();
                        endPacket.Deserialize(reader);
                        YargLogger.LogInfo("GameClientSession: GameEnd received");
                        Track(async () =>
                        {
                            await UniTask.SwitchToMainThread(_lifetimeCts.Token);
                            GameEnded?.Invoke();
                        }).Forget();
                        break;
                    }
                    case PacketOpcode.Pong:
                    {
                        var pongPacket = new PongPacket();
                        pongPacket.Deserialize(reader);
                        // Invoke synchronously on the poll thread. ServerClockSync's handler
                        // is small and lock-free; main-thread serialization would only add
                        // jitter to the sample.
                        PongReceived?.Invoke(pongPacket.ClientTickMs, pongPacket.ServerUtcMs, receiveLocalUtcMs);
                        break;
                    }
                    default:
                        YargLogger.LogWarning($"GameClientSession: unexpected opcode {opcode}");
                        break;
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }
            finally
            {
                reader.Recycle();
            }
        }

        private static EngineInputRecord[] ToArray(IReadOnlyList<EngineInputRecord> list)
        {
            if (list is EngineInputRecord[] arr) return arr;
            var copy = new EngineInputRecord[list.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = list[i];
            return copy;
        }

        private async UniTask PollLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var manager = _manager;
                    if (manager == null) break;
                    manager.PollEvents();
                    await UniTask.Delay(PollInterval, cancellationToken: ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                YargLogger.LogException(ex);
            }
        }
    }
}
