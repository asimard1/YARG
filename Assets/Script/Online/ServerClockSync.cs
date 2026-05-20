using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using YARG.Core.Logging;

namespace YARG.Online
{
    /// <summary>
    /// Measures and tracks the offset between this client's wall clock and the game
    /// server's wall clock via UDP Ping/Pong probes. Used by
    /// <see cref="OnlineSessionDirector"/> to translate the server-issued
    /// <c>SongOriginUtcMs</c> into a delay against the local clock without trusting
    /// that the two clocks are in agreement.
    ///
    /// One instance per <see cref="GameClientSession"/>. Constructed by
    /// <see cref="LobbyGameOrchestrator"/> alongside the session, disposed before
    /// the session is torn down. <see cref="Current"/> is a handoff slot so the
    /// gameplay scene's MonoBehaviours can read the synced offset without a ctor
    /// dependency across the menu→gameplay scene boundary.
    ///
    /// Sample math (Cristian's algorithm — assumes symmetric latency):
    ///   rtt    = receiveLocalUtcMs - clientTickMs
    ///   offset = serverUtcMs + rtt/2 - receiveLocalUtcMs
    /// Then estimated server time at any later instant is
    ///   serverEstimatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ClockOffsetMs.
    /// </summary>
    public sealed class ServerClockSync : IDisposable
    {
        public static ServerClockSync Current { get; private set; }

        private readonly GameClientSession _session;
        private readonly object _lock = new();
        private readonly List<Sample> _samples = new();
        private long _clockOffsetMs;
        private long _bestRttMs;
        private bool _isSynced;
        private int _disposed;

        private readonly struct Sample
        {
            public readonly long RttMs;
            public readonly long OffsetMs;
            public Sample(long rttMs, long offsetMs) { RttMs = rttMs; OffsetMs = offsetMs; }
        }

        /// <summary>True once <see cref="RunSyncBurstAsync"/> has produced at least the
        /// minimum number of usable samples.</summary>
        public bool IsSynced { get { lock (_lock) return _isSynced; } }

        /// <summary>Estimated <c>serverUtcMs - localUtcMs</c>. Add to a local wall-clock
        /// reading to estimate the server's wall-clock at the same instant. Zero until
        /// <see cref="IsSynced"/> is true.</summary>
        public long ClockOffsetMs { get { lock (_lock) return _clockOffsetMs; } }

        /// <summary>RTT of the sample that produced the current offset estimate.
        /// Half of this is roughly the worst-case error bound on
        /// <see cref="ClockOffsetMs"/>.</summary>
        public long BestRttMs { get { lock (_lock) return _bestRttMs; } }

        /// <summary>Estimated error bound on the offset, in milliseconds. Cristian's
        /// algorithm bounds the error at RTT/2 under the symmetric-latency assumption.</summary>
        public double EstimatedErrorMs { get { lock (_lock) return _bestRttMs / 2.0; } }

        /// <summary>Server's estimated wall-clock UTC ms at the moment of this call.
        /// Reads the local clock and applies the cached offset; if <see cref="IsSynced"/>
        /// is false the offset is zero and this degrades to the raw local clock.</summary>
        public long NowServerUtcMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ClockOffsetMs;

        public ServerClockSync(GameClientSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _session.PongReceived += OnPongReceived;

            if (Current != null)
            {
                YargLogger.LogWarning(
                    "ServerClockSync: Current is already set when constructing a new instance; " +
                    "overwriting. (Did a previous sync fail to Dispose?)");
            }
            Current = this;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _session.PongReceived -= OnPongReceived;
            if (Current == this) Current = null;
        }

        /// <summary>
        /// Send a burst of pings and pick the sample with the lowest RTT as the
        /// offset estimate. Returns true if at least <c>minSamples</c> pongs landed
        /// within the burst window; false otherwise (callers should warn and fall
        /// back to the raw local clock).
        /// </summary>
        public async UniTask<bool> RunSyncBurstAsync(
            int samples = 8,
            int spacingMs = 50,
            int minSamples = 3,
            CancellationToken ct = default)
        {
            if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));
            if (spacingMs < 0) throw new ArgumentOutOfRangeException(nameof(spacingMs));
            if (minSamples < 1) throw new ArgumentOutOfRangeException(nameof(minSamples));

            lock (_lock)
            {
                _samples.Clear();
                _isSynced = false;
            }

            YargLogger.LogFormatInfo(
                "ServerClockSync: starting burst — samples={0}, spacingMs={1}", samples, spacingMs);

            for (int i = 0; i < samples; i++)
            {
                ct.ThrowIfCancellationRequested();
                _session.SendPing(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (i < samples - 1)
                {
                    await UniTask.Delay(spacingMs, DelayType.Realtime, cancellationToken: ct);
                }
            }

            // Wait for replies. Budget is the burst length plus enough slack for the last
            // pong to come back over a typical WAN — 500ms is generous for the usual case
            // and not painful when sync genuinely fails.
            int waitBudgetMs = samples * spacingMs + 500;
            int waitedMs = 0;
            const int pollMs = 20;
            while (waitedMs < waitBudgetMs)
            {
                ct.ThrowIfCancellationRequested();
                int count;
                lock (_lock) count = _samples.Count;
                if (count >= samples) break;
                await UniTask.Delay(pollMs, DelayType.Realtime, cancellationToken: ct);
                waitedMs += pollMs;
            }

            Sample[] snapshot;
            lock (_lock)
            {
                snapshot = _samples.ToArray();
            }

            if (snapshot.Length < minSamples)
            {
                YargLogger.LogFormatWarning(
                    "ServerClockSync: burst produced {0}/{1} samples (min {2}); falling back to local clock",
                    snapshot.Length, samples, minSamples);
                return false;
            }

            // Cristian's algorithm: take the lowest-RTT sample. Network jitter only ever
            // inflates RTT, so the minimum is closest to the true symmetric-latency case.
            int bestIdx = 0;
            for (int i = 1; i < snapshot.Length; i++)
            {
                if (snapshot[i].RttMs < snapshot[bestIdx].RttMs) bestIdx = i;
            }
            var best = snapshot[bestIdx];

            long minRtt = snapshot[0].RttMs, maxRtt = snapshot[0].RttMs;
            long sumRtt = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                long r = snapshot[i].RttMs;
                if (r < minRtt) minRtt = r;
                if (r > maxRtt) maxRtt = r;
                sumRtt += r;
            }
            long avgRtt = sumRtt / snapshot.Length;

            lock (_lock)
            {
                _clockOffsetMs = best.OffsetMs;
                _bestRttMs = best.RttMs;
                _isSynced = true;
            }

            YargLogger.LogFormatInfo(
                "ServerClockSync: synced — offsetMs={0}, bestRttMs={1}, rtt(min/avg/max)={2}/{3}/{4}, samples={5}",
                best.OffsetMs, best.RttMs, minRtt, avgRtt, maxRtt, snapshot.Length);

            return true;
        }

        // Called on the LiteNetLib poll thread. Cheap and lock-only — must not block.
        private void OnPongReceived(long clientTickMs, long serverUtcMs, long receiveLocalUtcMs)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            long rtt = receiveLocalUtcMs - clientTickMs;
            if (rtt < 0)
            {
                // Local clock went backwards mid-burst (NTP step or manual change). The sample
                // is unusable; drop it rather than poisoning the burst.
                return;
            }
            long offset = serverUtcMs + rtt / 2 - receiveLocalUtcMs;

            lock (_lock)
            {
                _samples.Add(new Sample(rtt, offset));
            }
        }
    }
}
