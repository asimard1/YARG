using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
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

        private readonly object _lock = new();
        private NetManager _manager;
        private EventBasedNetListener _listener;
        private NetPeer _serverPeer;
        private TaskCompletionSource<bool> _connectOutcome;

        private readonly CancellationTokenSource _lifetimeCts = new();
        private int _disposed; // 0 = alive, 1 = disposing/disposed

        // Count of fire-and-forget Track() bodies currently in flight. DisposeAsync
        // waits for this to drain to zero before tearing down the NetManager, so a
        // callback racing with shutdown can't fire an event after disposal.
        private int _inflightHandlers;

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

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer reports a
        /// missed note. Args: <c>peerId</c>, <c>noteIndex</c> (into the chart's flattened
        /// note list for the remote peer's instrument+difficulty), <c>songTime</c>.
        /// Stays on the receive thread because the prediction layer needs the lowest
        /// possible event-arrival latency to decide whether a miss lands inside the
        /// commit window or triggers a rollback.</summary>
        public event Action<int, int, double> NoteMissedReceived;

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer activates
        /// star power. Args: <c>peerId</c>, <c>songTime</c> at which activation occurred.
        /// Stays on the receive thread for the same latency reason as
        /// <see cref="NoteMissedReceived"/>.</summary>
        public event Action<int, double> StarPowerActivatedReceived;

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer's whammy
        /// axis changes. Args: <c>peerId</c>, <c>songTime</c>, <c>value</c> in [0,1].
        /// Stays on the receive thread for the same latency reason as
        /// <see cref="NoteMissedReceived"/>.</summary>
        public event Action<int, double, float> WhammyReceived;

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer
        /// emits a vocal pitch sample. Args: <c>peerId</c>, <c>songTime</c>,
        /// <c>pitchMidi</c>, <c>isSinging</c>. The director's per-peer simulator
        /// buffers samples and the visual layer interpolates between them so
        /// the on-track pitch blob slides smoothly between packets.</summary>
        public event Action<int, double, float, bool> VocalPitchReceived;

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer
        /// releases a sustain early. Args: <c>peerId</c>, <c>noteIndex</c>
        /// (root note of the sustain), <c>songTime</c> at which release
        /// happened. Stays on the receive thread because the prediction
        /// layer keys its rollback decision off event-arrival time.</summary>
        public event Action<int, int, double> SustainReleasedReceived;

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer
        /// overstrums. Args: <c>peerId</c>, <c>songTime</c> at which the
        /// overstrum fired. Stays on the receive thread for the same
        /// rationale as <see cref="SustainReleasedReceived"/>.</summary>
        public event Action<int, double> OverstrumReceived;

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer
        /// reports a hit. Paired with <see cref="NoteMissedReceived"/> —
        /// together they form the per-note outcome stream used by the
        /// prediction layer's Markov-1 model.</summary>
        public event Action<int, int, double> NoteHitReceived;

        /// <summary>Fired on the LiteNetLib receive thread when a remote peer
        /// delivers a periodic authoritative engine-state snapshot. Args:
        /// <c>peerId</c>, <c>songTime</c> (sender's engine time at capture),
        /// <c>snapshotKind</c> (1=Guitar, ...), <c>snapshotData</c> (opaque
        /// bytes, deserialized by <see cref="EngineSnapshotSerializer"/>).
        /// The receiver restores the mirror engine to the sender's exact
        /// state, eliminating any drift accumulated since the previous
        /// snapshot.</summary>
        public event Action<int, double, byte, byte[]> EngineStateSnapshotReceived;

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
                // UnsyncedEvents: LiteNetLib fires every callback directly on its
                // receive thread instead of buffering for PollEvents(). Removes
                // the per-tick latency floor between packet arrival and our
                // handler. We already marshal to the Unity main thread via
                // Track + SwitchToMainThread for handlers that need it.
                _manager = new NetManager(_listener)
                {
                    UnconnectedMessagesEnabled = false,
                    UnsyncedEvents = true,
                };
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

        public void SendLoadout(
            InstrumentId instrument, DifficultyId difficulty, Guid enginePreset,
            float noteSpeed, ulong modifiers, byte[] chartHash)
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                throw new InvalidOperationException("GameClientSession not connected; cannot send loadout.");
            }
            if (chartHash == null || chartHash.Length != SetLoadoutPacket.ChartHashLength)
            {
                throw new ArgumentException(
                    $"chartHash must be exactly {SetLoadoutPacket.ChartHashLength} bytes (got {chartHash?.Length ?? 0}).",
                    nameof(chartHash));
            }

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.SetLoadout, new SetLoadoutPacket
            {
                Instrument = instrument,
                Difficulty = difficulty,
                EnginePreset = enginePreset,
                NoteSpeed = noteSpeed,
                Modifiers = modifiers,
                ChartHash = chartHash,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            YargLogger.LogInfo($"GameClientSession: SendLoadout instrument={instrument} difficulty={difficulty} noteSpeed={noteSpeed} modifiers=0x{modifiers:X}");
        }

        /// <summary>
        /// Retract a previously-sent loadout (DifficultySelect "Unready"). Server drops
        /// the stored loadout if the game hasn't started yet; otherwise the request is a
        /// silent no-op (you can't unready once the game is in progress).
        /// </summary>
        public void SendUnready()
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                YargLogger.LogWarning("GameClientSession: SendUnready called while disconnected; ignoring.");
                return;
            }

            var writer = new NetDataWriter();
            writer.Put((byte) PacketOpcode.ClearLoadout);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
            YargLogger.LogInfo("GameClientSession: SendUnready (ClearLoadout)");
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

        /// <summary>Send a single missed-note event to the server for fan-out. Called
        /// the instant the local engine reports a miss — NOT batched, because the
        /// receivers' prediction layer is sensitive to event arrival time. Safe to
        /// call from any thread.</summary>
        public void SendNoteMissed(int noteIndex, double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.NoteMissed, new NoteMissedPacket
            {
                PeerId = 0,
                NoteIndex = noteIndex,
                SongTime = songTime,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Send a star-power activation event to the server for fan-out.
        /// Called when the local player activates SP. Safe to call from any thread.</summary>
        public void SendStarPowerActivated(double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.StarPowerActivated, new StarPowerActivatedPacket
            {
                PeerId = 0,
                SongTime = songTime,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Send a whammy axis sample to the server for fan-out. Called from
        /// the local engine's whammy input handler — NOT batched. Senders should apply
        /// their own change-threshold (e.g. quantize to 256 levels) before calling so
        /// the packet rate stays bounded. Safe to call from any thread.</summary>
        public void SendWhammy(double songTime, float value)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.Whammy, new WhammyPacket
            {
                PeerId = 0,
                SongTime = songTime,
                Value = value,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Send a vocal pitch sample for the singer's on-track blob position.
        /// Senders should rate-limit (~20 Hz is plenty — receivers interpolate between
        /// samples). pitchMidi matches VocalsEngine.PitchSang. isSinging disambiguates
        /// a valid 0 MIDI value from "silence" on the wire.</summary>
        public void SendVocalPitch(double songTime, float pitchMidi, bool isSinging)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.VocalPitch, new VocalPitchPacket
            {
                PeerId = 0,
                SongTime = songTime,
                PitchMidi = pitchMidi,
                IsSinging = isSinging,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Send an early-sustain-release event to the server for
        /// fan-out. Called the instant the local engine reports a sustain
        /// was dropped before its natural end. Doesn't break combo on the
        /// receiver — only stops sustain scoring at <paramref name="songTime"/>.
        /// Safe to call from any thread.</summary>
        public void SendSustainReleased(int noteIndex, double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.SustainReleased, new SustainReleasedPacket
            {
                PeerId = 0,
                NoteIndex = noteIndex,
                SongTime = songTime,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Send an overstrum event to the server for fan-out.
        /// Called the instant the local engine reports an overstrum.
        /// Breaks combo, drops active sustains, strips SP from the current
        /// note, and docks the rock meter on receivers. Safe to call from
        /// any thread.</summary>
        public void SendOverstrum(double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.Overstrum, new OverstrumPacket
            {
                PeerId = 0,
                SongTime = songTime,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Send a note-hit event to the server for fan-out.
        /// Paired with <see cref="SendNoteMissed"/> — together they form
        /// the per-note outcome stream that drives the receiver's
        /// prediction model. Safe to call from any thread.</summary>
        public void SendNoteHit(int noteIndex, double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.NoteHit, new NoteHitPacket
            {
                PeerId = 0,
                NoteIndex = noteIndex,
                SongTime = songTime,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
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

        /// <summary>Send an authoritative engine-state snapshot to the relay
        /// for fan-out. Receivers restore their mirror engine to the
        /// sender's exact state, eliminating any drift accumulated since the
        /// previous snapshot. Sent periodically by the local player update
        /// path; one final snapshot is also sent immediately before
        /// GameComplete so receivers can render the results screen using
        /// authoritative final values. Safe to call from any thread.</summary>
        public void SendEngineStateSnapshot(double songTime, byte snapshotKind, byte[] snapshotData)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.EngineStateSnapshot, new EngineStateSnapshotPacket
            {
                PeerId = 0,
                SongTime = songTime,
                SnapshotKind = snapshotKind,
                SnapshotData = snapshotData,
            });
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public async UniTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            YargLogger.LogInfo("GameClientSession: disposing");

            try { _lifetimeCts.Cancel(); } catch { }

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
                        // Invoke synchronously on the receive thread. ServerClockSync's
                        // handler is small and lock-free; a main-thread hop would only
                        // add frame-time jitter to the very thing we're measuring.
                        PongReceived?.Invoke(pongPacket.ClientTickMs, pongPacket.ServerUtcMs, receiveLocalUtcMs);
                        break;
                    }
                    case PacketOpcode.NoteMissed:
                    {
                        var missPacket = new NoteMissedPacket();
                        missPacket.Deserialize(reader);
                        int subCount = NoteMissedReceived?.GetInvocationList().Length ?? 0;
                        YargLogger.LogFormatDebug(
                            "Prediction[wire-recv] NoteMissed: peer={0} noteIndex={1} songTime={2:0.000} subs={3}",
                            missPacket.PeerId, missPacket.NoteIndex, missPacket.SongTime, subCount);
                        // Invoke synchronously on the receive thread. The prediction
                        // layer's commit-window decision is keyed off event arrival time;
                        // a main-thread hop would push every miss past the deadline by
                        // up to one frame and inflate the rollback rate for no benefit.
                        NoteMissedReceived?.Invoke(missPacket.PeerId, missPacket.NoteIndex, missPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.StarPowerActivated:
                    {
                        var spPacket = new StarPowerActivatedPacket();
                        spPacket.Deserialize(reader);
                        int subCount = StarPowerActivatedReceived?.GetInvocationList().Length ?? 0;
                        YargLogger.LogFormatDebug(
                            "Prediction[wire-recv] StarPowerActivated: peer={0} songTime={1:0.000} subs={2}",
                            spPacket.PeerId, spPacket.SongTime, subCount);
                        // Same rationale as NoteMissed: stays on the receive thread to
                        // keep activation alignment as tight as possible.
                        StarPowerActivatedReceived?.Invoke(spPacket.PeerId, spPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.Whammy:
                    {
                        var whammyPacket = new WhammyPacket();
                        whammyPacket.Deserialize(reader);
                        int subCount = WhammyReceived?.GetInvocationList().Length ?? 0;
                        YargLogger.LogFormatDebug(
                            "Prediction[wire-recv] Whammy: peer={0} songTime={1:0.000} value={2:0.00} subs={3}",
                            whammyPacket.PeerId, whammyPacket.SongTime, whammyPacket.Value, subCount);
                        // Same rationale as NoteMissed: stays on the receive thread.
                        WhammyReceived?.Invoke(whammyPacket.PeerId, whammyPacket.SongTime, whammyPacket.Value);
                        break;
                    }
                    case PacketOpcode.VocalPitch:
                    {
                        var vpPacket = new VocalPitchPacket();
                        vpPacket.Deserialize(reader);
                        // Same rationale as Whammy: stays on the receive thread; the
                        // director queues the sample for next-tick consumption.
                        VocalPitchReceived?.Invoke(
                            vpPacket.PeerId, vpPacket.SongTime, vpPacket.PitchMidi, vpPacket.IsSinging);
                        break;
                    }
                    case PacketOpcode.SustainReleased:
                    {
                        var releasePacket = new SustainReleasedPacket();
                        releasePacket.Deserialize(reader);
                        int subCount = SustainReleasedReceived?.GetInvocationList().Length ?? 0;
                        YargLogger.LogFormatDebug(
                            "Prediction[wire-recv] SustainReleased: peer={0} noteIndex={1} songTime={2:0.000} subs={3}",
                            releasePacket.PeerId, releasePacket.NoteIndex, releasePacket.SongTime, subCount);
                        SustainReleasedReceived?.Invoke(
                            releasePacket.PeerId, releasePacket.NoteIndex, releasePacket.SongTime);
                        break;
                    }
                    case PacketOpcode.Overstrum:
                    {
                        var overPacket = new OverstrumPacket();
                        overPacket.Deserialize(reader);
                        int subCount = OverstrumReceived?.GetInvocationList().Length ?? 0;
                        YargLogger.LogFormatDebug(
                            "Prediction[wire-recv] Overstrum: peer={0} songTime={1:0.000} subs={2}",
                            overPacket.PeerId, overPacket.SongTime, subCount);
                        OverstrumReceived?.Invoke(overPacket.PeerId, overPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.NoteHit:
                    {
                        var hitPacket = new NoteHitPacket();
                        hitPacket.Deserialize(reader);
                        int subCount = NoteHitReceived?.GetInvocationList().Length ?? 0;
                        YargLogger.LogFormatDebug(
                            "Prediction[wire-recv] NoteHit: peer={0} noteIndex={1} songTime={2:0.000} subs={3}",
                            hitPacket.PeerId, hitPacket.NoteIndex, hitPacket.SongTime, subCount);
                        NoteHitReceived?.Invoke(hitPacket.PeerId, hitPacket.NoteIndex, hitPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.EngineStateSnapshot:
                    {
                        var snapPacket = new EngineStateSnapshotPacket();
                        snapPacket.Deserialize(reader);
                        int subCount = EngineStateSnapshotReceived?.GetInvocationList().Length ?? 0;
                        YargLogger.LogFormatDebug(
                            "Prediction[wire-recv] EngineStateSnapshot: peer={0} songTime={1:0.000} kind={2} bytes={3} subs={4}",
                            snapPacket.PeerId, snapPacket.SongTime, snapPacket.SnapshotKind,
                            snapPacket.SnapshotData?.Length ?? 0, subCount);
                        EngineStateSnapshotReceived?.Invoke(
                            snapPacket.PeerId, snapPacket.SongTime,
                            snapPacket.SnapshotKind, snapPacket.SnapshotData);
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

    }
}
