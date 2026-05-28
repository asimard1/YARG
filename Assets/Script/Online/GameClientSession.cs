using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using YARG.Core.Engine;
using YARG.Core.Logging;
using YARG.Online.Game.Contracts.Enums;
using YARG.Online.Game.Contracts.Packets;

namespace YARG.Online
{
    /// <summary>
    /// LiteNetLib UDP client for an in-game session. Events are raised on the Unity main thread.
    /// </summary>
    public sealed class GameClientSession
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

        private readonly object _lock = new();
        private NetManager _manager;
        private EventBasedNetListener _listener;
        private NetPeer _serverPeer;
        private TaskCompletionSource<bool> _connectOutcome;

        // Reused across all Send* calls. Main-thread-only: every Send* runs on the Unity main thread
        private readonly NetDataWriter _sendWriter = new();

        private readonly CancellationTokenSource _lifetimeCts = new();
        private int _disposed; // 0 = alive, 1 = disposing/disposed
        private int _inflightHandlers; // Track() bodies in flight; DisposeAsync drains to zero

        public GameClientSession()
        {
            YargLogger.LogInfo("GameClientSession: created");
        }

        public bool IsConnected
        {
            get { lock (_lock) return _manager != null && _serverPeer != null; }
        }

        /// <summary>Fired (main thread) when GameStart is received with peer loadouts.</summary>
        public event Action<PeerLoadout[]> GameStarted;

        /// <summary>Fired (main thread) when GameStartCue arrives with the song origin timestamp.</summary>
        public event Action<long, int> StartCueReceived;

        /// <summary>Fired (receive thread) on Pong. Args: clientTickMs, serverUtcMs, receiveLocalUtcMs.
        /// Stays on receive thread to avoid adding frame-time jitter to clock-sync measurements.</summary>
        public event Action<long, long, long> PongReceived;

        /// <summary>Fired (main thread) when a remote peer drops mid-session.</summary>
        public event Action<int> RemotePeerLeft;

        /// <summary>Fired (main thread) when the server broadcasts GameEnd.</summary>
        public event Action GameEnded;

        /// <summary>Fired (main thread) when the UDP connection drops.</summary>
        public event Action Disconnected;

        /// <summary>Fired (receive thread) on remote NoteMissed. Args: peerId, noteIndex, songTime.</summary>
        public event Action<int, int, double> NoteMissedReceived;

        /// <summary>Fired (receive thread) on remote StarPowerActivated. Args: peerId, songTime.</summary>
        public event Action<int, double> StarPowerActivatedReceived;

        /// <summary>Fired (receive thread) on remote Whammy. Args: peerId, songTime, value [0,1].</summary>
        public event Action<int, double, float> WhammyReceived;

        /// <summary>Fired (receive thread) on remote VocalPitch. Args: peerId, songTime, pitchMidi, isSinging.</summary>
        public event Action<int, double, float, bool> VocalPitchReceived;
        public event Action<int, double, int, float>  FreePlayInputReceived;

        /// <summary>Fired (receive thread) on remote SustainReleased. Args: peerId, noteIndex, songTime.</summary>
        public event Action<int, int, double> SustainReleasedReceived;

        /// <summary>Fired (receive thread) on remote Overstrum. Args: peerId, songTime.</summary>
        public event Action<int, double> OverstrumReceived;

        /// <summary>Fired (receive thread) on remote NoteHit. Args: peerId, noteIndex, songTime.</summary>
        public event Action<int, int, double> NoteHitReceived;

        /// <summary>Fired (receive thread) on remote EngineStateSnapshot. Args: peerId, songTime, kind, data.</summary>
        public event Action<int, double, byte, byte[]> EngineStateSnapshotReceived;

        /// <summary>Connect to the game server. Single-shot; throws if already started.</summary>
        public async UniTask<bool> ConnectAsync(IPEndPoint endpoint, string jwt, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_manager != null)
                {
                    throw new InvalidOperationException(
                        "GameClientSession already has an active NetManager -- call DisposeAsync first.");
                }

                _listener = new EventBasedNetListener();
                _manager = new NetManager(_listener)
                {
                    UnconnectedMessagesEnabled = false,
                    UnsyncedEvents = true,
                    // 15s tolerance so Mono stop-the-world GC pauses during scene
                    // load don't trigger server disconnect (mirrors GameNetworkService).
                    PingInterval = 1000,
                    DisconnectTimeout = 15000,
                };
                _connectOutcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                _listener.PeerConnectedEvent += peer =>
                {
                    _serverPeer = peer;
                    YargLogger.LogInfo($"GameClientSession: connected -- peerId={peer.Id}");
                    _connectOutcome.TrySetResult(true);
                };
                _listener.PeerDisconnectedEvent += (peer, info) =>
                {
                    YargLogger.LogInfo($"GameClientSession: disconnected -- reason={info.Reason}");
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
                    YargLogger.LogWarning($"GameClientSession: network error from {ep} -- {err}");
            }

            if (!_manager.Start())
            {
                YargLogger.LogError("GameClientSession: NetManager failed to start.");
                await DisposeAsync();
                return false;
            }

            var writer = new NetDataWriter();
            writer.Put(jwt);
            _manager.Connect(endpoint, writer);

            YargLogger.LogInfo($"GameClientSession: connecting to {endpoint}...");

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

        private void SendToServer<T>(NetPeer peer, PacketOpcode opcode, T packet)
            where T : INetSerializable
        {
            _sendWriter.Reset();
            GamePacketWriter.Write(_sendWriter, opcode, packet);
            peer.Send(_sendWriter, DeliveryMethod.ReliableOrdered);
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
            if (chartHash is not { Length: SetLoadoutPacket.ChartHashLength })
            {
                throw new ArgumentException(
                    $"chartHash must be exactly {SetLoadoutPacket.ChartHashLength} bytes (got {chartHash?.Length ?? 0}).",
                    nameof(chartHash));
            }

            SendToServer(peer, PacketOpcode.SetLoadout, new SetLoadoutPacket
            {
                Instrument = instrument,
                Difficulty = difficulty,
                EnginePreset = enginePreset,
                NoteSpeed = noteSpeed,
                Modifiers = modifiers,
                ChartHash = chartHash,
            });
            YargLogger.LogInfo($"GameClientSession: SendLoadout instrument={instrument} difficulty={difficulty} noteSpeed={noteSpeed} modifiers=0x{modifiers:X}");
        }

        /// <summary>Retract a previously-sent loadout (Unready). No-op if game already started.</summary>
        public void SendUnready()
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                YargLogger.LogWarning("GameClientSession: SendUnready called while disconnected; ignoring.");
                return;
            }

            _sendWriter.Reset();
            _sendWriter.Put((byte) PacketOpcode.ClearLoadout);
            peer.Send(_sendWriter, DeliveryMethod.ReliableOrdered);
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
            SendToServer(peer, PacketOpcode.PeerReady, new PeerReadyPacket());
            YargLogger.LogInfo("GameClientSession: SendPeerReady");
        }

        /// <summary>Send a clock-sync ping. <paramref name="clientTickMs"/> is echoed back for RTT measurement.</summary>
        public void SendPing(long clientTickMs)
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                YargLogger.LogWarning("GameClientSession.SendPing: no server peer; dropping.");
                return;
            }
            SendToServer(peer, PacketOpcode.Ping, new PingPacket { ClientTickMs = clientTickMs });
        }

        /// <summary>Send a missed-note event for fan-out. Not batched -- latency-sensitive.</summary>
        public void SendNoteMissed(int noteIndex, double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.NoteMissed, new NoteMissedPacket
            {
                PeerId = 0,
                NoteIndex = noteIndex,
                SongTime = songTime,
            });
        }

        /// <summary>Send a star-power activation event for fan-out.</summary>
        public void SendStarPowerActivated(double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.StarPowerActivated, new StarPowerActivatedPacket
            {
                PeerId = 0,
                SongTime = songTime,
            });
        }

        /// <summary>Send a whammy axis sample for fan-out. Callers should apply a change-threshold.</summary>
        public void SendWhammy(double songTime, float value)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.Whammy, new WhammyPacket
            {
                PeerId = 0,
                SongTime = songTime,
                Value = value,
            });
        }

        /// <summary>Send a vocal pitch sample. Rate-limit to ~20 Hz; receivers interpolate.</summary>
        public void SendVocalPitch(double songTime, float pitchMidi, bool isSinging)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.VocalPitch, new VocalPitchPacket
            {
                PeerId = 0,
                SongTime = songTime,
                PitchMidi = pitchMidi,
                IsSinging = isSinging,
            });
        }

        /// <summary>Fan-out a free-play input for remote highway visuals during BRE / activator phrases.</summary>
        public void SendFreePlayInput(double songTime, int action, float velocity)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.FreePlayInput, new FreePlayInputPacket
            {
                PeerId = 0,
                SongTime = songTime,
                Action = action,
                Velocity = velocity,
            });
        }

        /// <summary>Send an early-sustain-release event for fan-out.</summary>
        public void SendSustainReleased(int noteIndex, double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.SustainReleased, new SustainReleasedPacket
            {
                PeerId = 0,
                NoteIndex = noteIndex,
                SongTime = songTime,
            });
        }

        /// <summary>Send an overstrum event for fan-out.</summary>
        public void SendOverstrum(double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.Overstrum, new OverstrumPacket
            {
                PeerId = 0,
                SongTime = songTime,
            });
        }

        /// <summary>Send a note-hit event for fan-out.</summary>
        public void SendNoteHit(int noteIndex, double songTime)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            SendToServer(peer, PacketOpcode.NoteHit, new NoteHitPacket
            {
                PeerId = 0,
                NoteIndex = noteIndex,
                SongTime = songTime,
            });
        }

        public void SendGameComplete()
        {
            var peer = _serverPeer;
            if (peer == null)
            {
                YargLogger.LogWarning("GameClientSession.SendGameComplete: no server peer; dropping.");
                return;
            }
            SendToServer(peer, PacketOpcode.GameComplete, new GameCompletePacket());
            YargLogger.LogInfo("GameClientSession: SendGameComplete");
        }

        /// <summary>Send an authoritative engine-state snapshot for fan-out.</summary>
        public void SendEngineStateSnapshot(double songTime, byte snapshotKind, EngineSnapshot snapshot)
        {
            var peer = _serverPeer;
            if (peer == null) return;

            // TODO: This code is ugly af, needs to be cleaned up

            // Hand-written framing -- must stay byte-for-byte identical to
            // EngineStateSnapshotPacket.Serialize (PeerId, SongTime, SnapshotKind,
            // then a ushort-length-prefixed opaque blob). We serialize the snapshot
            // straight into the shared send buffer and backpatch the length, avoiding
            // the intermediate byte[] + packet allocation.
            _sendWriter.Reset();
            _sendWriter.Put((byte) PacketOpcode.EngineStateSnapshot);
            _sendWriter.Put(0);                 // PeerId -- sender always sends 0 (server stamps real id)
            _sendWriter.Put(songTime);
            _sendWriter.Put(snapshotKind);

            int lengthPos = _sendWriter.Length; // reserve the ushort length slot
            _sendWriter.Put((ushort) 0);
            int payloadStart = _sendWriter.Length;
            EngineSnapshotSerializer.Serialize(_sendWriter, snapshot);
            int payloadEnd = _sendWriter.Length;

            int payloadLen = payloadEnd - payloadStart;
            if (payloadLen > ushort.MaxValue)
                throw new InvalidOperationException(
                    $"EngineStateSnapshot payload {payloadLen} exceeds {ushort.MaxValue}-byte ushort length limit.");

            _sendWriter.SetPosition(lengthPos); // backpatch real length, then restore
            _sendWriter.Put((ushort) payloadLen);
            _sendWriter.SetPosition(payloadEnd);

            peer.Send(_sendWriter, DeliveryMethod.ReliableOrdered);
        }

        public async UniTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            YargLogger.LogInfo("GameClientSession: disposing");

            try { _lifetimeCts.Cancel(); } catch { }

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
                catch (Exception ex) { YargLogger.LogWarning($"GameClientSession: stop -- {ex.Message}"); }
            }

            _lifetimeCts.Dispose();

            YargLogger.LogInfo("GameClientSession: disposed");
        }

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
            // Sample receive time before parsing for accurate clock-sync RTT.
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
                        YargLogger.LogInfo($"GameClientSession: GameStart received -- loadouts={loadouts.Length}");
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
                        YargLogger.LogInfo($"GameClientSession: GameStartCue received -- origin={originUtcMs}");
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
                        PongReceived?.Invoke(pongPacket.ClientTickMs, pongPacket.ServerUtcMs, receiveLocalUtcMs);
                        break;
                    }
                    case PacketOpcode.NoteMissed:
                    {
                        var missPacket = new NoteMissedPacket();
                        missPacket.Deserialize(reader);
                        if (YargLogger.IsLevelEnabled(LogLevel.Debug))
                        {
                            int subCount = NoteMissedReceived?.GetInvocationList().Length ?? 0;
                            YargLogger.LogFormatDebug(
                                "Prediction[wire-recv] NoteMissed: peer={0} noteIndex={1} songTime={2:0.000} subs={3}",
                                missPacket.PeerId, missPacket.NoteIndex, missPacket.SongTime, subCount);
                        }
                        NoteMissedReceived?.Invoke(missPacket.PeerId, missPacket.NoteIndex, missPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.StarPowerActivated:
                    {
                        var spPacket = new StarPowerActivatedPacket();
                        spPacket.Deserialize(reader);
                        if (YargLogger.IsLevelEnabled(LogLevel.Debug))
                        {
                            int subCount = StarPowerActivatedReceived?.GetInvocationList().Length ?? 0;
                            YargLogger.LogFormatDebug(
                                "Prediction[wire-recv] StarPowerActivated: peer={0} songTime={1:0.000} subs={2}",
                                spPacket.PeerId, spPacket.SongTime, subCount);
                        }
                        StarPowerActivatedReceived?.Invoke(spPacket.PeerId, spPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.Whammy:
                    {
                        var whammyPacket = new WhammyPacket();
                        whammyPacket.Deserialize(reader);
                        if (YargLogger.IsLevelEnabled(LogLevel.Debug))
                        {
                            int subCount = WhammyReceived?.GetInvocationList().Length ?? 0;
                            YargLogger.LogFormatDebug(
                                "Prediction[wire-recv] Whammy: peer={0} songTime={1:0.000} value={2:0.00} subs={3}",
                                whammyPacket.PeerId, whammyPacket.SongTime, whammyPacket.Value, subCount);
                        }
                        WhammyReceived?.Invoke(whammyPacket.PeerId, whammyPacket.SongTime, whammyPacket.Value);
                        break;
                    }
                    case PacketOpcode.VocalPitch:
                    {
                        var vpPacket = new VocalPitchPacket();
                        vpPacket.Deserialize(reader);
                        VocalPitchReceived?.Invoke(
                            vpPacket.PeerId, vpPacket.SongTime, vpPacket.PitchMidi, vpPacket.IsSinging);
                        break;
                    }
                    case PacketOpcode.FreePlayInput:
                    {
                        var fpPacket = new FreePlayInputPacket();
                        fpPacket.Deserialize(reader);
                        FreePlayInputReceived?.Invoke(
                            fpPacket.PeerId, fpPacket.SongTime, fpPacket.Action, fpPacket.Velocity);
                        break;
                    }
                    case PacketOpcode.SustainReleased:
                    {
                        var releasePacket = new SustainReleasedPacket();
                        releasePacket.Deserialize(reader);
                        if (YargLogger.IsLevelEnabled(LogLevel.Debug))
                        {
                            int subCount = SustainReleasedReceived?.GetInvocationList().Length ?? 0;
                            YargLogger.LogFormatDebug(
                                "Prediction[wire-recv] SustainReleased: peer={0} noteIndex={1} songTime={2:0.000} subs={3}",
                                releasePacket.PeerId, releasePacket.NoteIndex, releasePacket.SongTime, subCount);
                        }
                        SustainReleasedReceived?.Invoke(
                            releasePacket.PeerId, releasePacket.NoteIndex, releasePacket.SongTime);
                        break;
                    }
                    case PacketOpcode.Overstrum:
                    {
                        var overPacket = new OverstrumPacket();
                        overPacket.Deserialize(reader);
                        if (YargLogger.IsLevelEnabled(LogLevel.Debug))
                        {
                            int subCount = OverstrumReceived?.GetInvocationList().Length ?? 0;
                            YargLogger.LogFormatDebug(
                                "Prediction[wire-recv] Overstrum: peer={0} songTime={1:0.000} subs={2}",
                                overPacket.PeerId, overPacket.SongTime, subCount);
                        }
                        OverstrumReceived?.Invoke(overPacket.PeerId, overPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.NoteHit:
                    {
                        var hitPacket = new NoteHitPacket();
                        hitPacket.Deserialize(reader);
                        if (YargLogger.IsLevelEnabled(LogLevel.Debug))
                        {
                            int subCount = NoteHitReceived?.GetInvocationList().Length ?? 0;
                            YargLogger.LogFormatDebug(
                                "Prediction[wire-recv] NoteHit: peer={0} noteIndex={1} songTime={2:0.000} subs={3}",
                                hitPacket.PeerId, hitPacket.NoteIndex, hitPacket.SongTime, subCount);
                        }
                        NoteHitReceived?.Invoke(hitPacket.PeerId, hitPacket.NoteIndex, hitPacket.SongTime);
                        break;
                    }
                    case PacketOpcode.EngineStateSnapshot:
                    {
                        var snapPacket = new EngineStateSnapshotPacket();
                        snapPacket.Deserialize(reader);
                        if (YargLogger.IsLevelEnabled(LogLevel.Debug))
                        {
                            int subCount = EngineStateSnapshotReceived?.GetInvocationList().Length ?? 0;
                            YargLogger.LogFormatDebug(
                                "Prediction[wire-recv] EngineStateSnapshot: peer={0} songTime={1:0.000} kind={2} bytes={3} subs={4}",
                                snapPacket.PeerId, snapPacket.SongTime, snapPacket.SnapshotKind,
                                snapPacket.SnapshotData?.Length ?? 0, subCount);
                        }
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
