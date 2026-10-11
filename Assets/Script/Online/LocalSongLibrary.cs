using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core.Song;
using YARG.Menu.Persistent;
using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// Snapshots the local player's installed song hashes. The snapshot is streamed to the
    /// lobby hub (see <see cref="LobbyHubSession"/>) so the server can compute the lobby-wide
    /// shared library (intersection of every member's library).
    ///
    /// Each song contributes BOTH its strict hash and its gameplay hash (when known) to the
    /// same set. The server's intersection logic is untouched and untyped -- it just treats
    /// these as opaque strings -- so two players end up "sharing" a song if either value
    /// matches. Strict takes precedence automatically wherever it's checked downstream
    /// (queueing prefers it when both are present in the synced shared library).
    ///
    /// Gameplay hashes require a full LoadChart() per song, so they can't be computed eagerly
    /// for the whole library without hurting scan performance. Instead, this class backfills
    /// them in the background, starting the moment a scan finishes (not when a lobby is
    /// joined) so someone who scans in new songs and immediately hosts isn't starting from
    /// zero, and prioritizing whatever was just added, since that's what they're most likely
    /// about to try to play with someone else.
    /// </summary>
    internal static class LocalSongLibrary
    {
        private const int BACKFILL_BATCH_SIZE = 25;

        // Gap between each song's LoadChart(), not just between batches -- this is real,
        // back-to-back CPU + allocation work, and without a gap it would pin a thread-pool
        // worker (and generate steady GC pressure) continuously for however long a large
        // library takes to backfill.
        private static readonly TimeSpan SongDelay = TimeSpan.FromMilliseconds(50);

        // How often to re-check whether gameplay has ended while paused.
        private static readonly TimeSpan GameplayPollDelay = TimeSpan.FromSeconds(2);

        private static CancellationTokenSource _backfillCts;

        /// <summary>
        /// Fired on the main thread once a background gameplay-hash backfill completes and
        /// discovers at least one new gameplay hash. <see cref="LobbyHubSession"/>
        /// subscribes to this only while a lobby session is active, re-streaming the updated
        /// snapshot in response.
        /// </summary>
        public static event Action BackfillBatchCompleted;

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            SongContainer.OnSongsRefreshed += RestartBackfill;
        }

        public static void EnsureBackfillRunning()
        {
            if (_backfillCts != null && !_backfillCts.IsCancellationRequested)
            {
                return;
            }
            RestartBackfill();
        }

        private static void RestartBackfill()
        {
            // A fresh scan (manual rescan, or one that was already running) always
            // supersedes whatever the previous backfill pass was doing -- it may have
            // found brand-new songs, so start over rather than let a stale pass keep
            // running against a library that no longer matches SongContainer's state.
            _backfillCts?.Cancel();
            _backfillCts = new CancellationTokenSource();
            BackfillAsync(_backfillCts.Token).Forget();
        }

        /// <summary>
        /// Materialize every installed song hash on the calling thread. Must be called from
        /// the main thread -- the resulting array is what gets chunked and streamed, so the
        /// SignalR send thread never touches <see cref="SongContainer.SongsByHash"/>.
        /// </summary>
        public static string[] SnapshotLocalHashes()
        {
            var byHash = SongContainer.SongsByHash;
            var hashes = new List<string>(byHash.Count * 2);

            foreach (var kv in byHash)
            {
                string strict = kv.Key.ToString();
                hashes.Add(strict);

                if (GameplayHashCache.TryGet(strict, out var cachedGameplayHash))
                {
                    hashes.Add(cachedGameplayHash);
                }
            }

            return hashes.ToArray();
        }

        private static async UniTask BackfillAsync(CancellationToken ct)
        {
            // Always called from the main thread. SongsByHash is rebuilt by scans, and
            // SongsByGameplayHash / GameplayHashCache are plain dictionaries read from the UI,
            // so every read of the library and every write to those maps stays on the main
            // thread. Only the expensive LoadChart() + hash runs on the thread pool, below.
            GameplayHashCache.Flush(); // recreate file if it was deleted mid-session

            // Snapshotted once, up front, rather than enumerated live: SongsByHash gets
            // fully cleared and rebuilt by the next scan, which would otherwise risk a
            // "collection was modified" exception or working against half-rebuilt state.
            var librarySnapshot = SongContainer.SongsByHash.ToArray();

            // Pass 1: register every hash that is already cached, immediately and before any
            // throttled hashing. These were already streamed to the lobby (SnapshotLocalHashes
            // reads the same cache), so they must resolve locally right away -- otherwise a
            // cached song sitting behind hundreds of uncached ones is shared with the lobby
            // but can't be resolved by this client until the slow pass reaches it.
            bool anyNewHashes = false;
            var uncached = new List<KeyValuePair<HashWrapper, List<SongEntry>>>();
            foreach (var kv in librarySnapshot)
            {
                if (GameplayHashCache.TryGet(kv.Key.ToString(), out var cachedGameplayHash))
                {
                    SongContainer.RegisterGameplayHash(kv.Key, HashWrapper.FromString(cachedGameplayHash));
                    anyNewHashes = true;
                }
                else
                {
                    uncached.Add(kv);
                }
            }

            // Newest file first: someone who just scanned in new songs and jumps straight
            // into a lobby cares about THOSE getting a gameplay hash before older songs
            // that have likely already been backfilled in a previous session anyway.
            uncached.Sort((a, b) =>
                b.Value[0].GetLastWriteTime().CompareTo(a.Value[0].GetLastWriteTime()));

            int sinceLastBatch = 0;

            // Pass 2: compute the rest.
            foreach (var kv in uncached)
            {
                ct.ThrowIfCancellationRequested();

                string strict = kv.Key.ToString();

                // Fully step aside while the player is actually playing. This is exactly
                // the moment a GC pause or thread-pool contention would be most noticeable
                // -- and nobody's about to join a lobby mid-song anyway, so there's no
                // downside to letting this wait.
                while (GlobalVariables.Instance.CurrentScene is SceneIndex.Gameplay)
                {
                    await UniTask.Delay(GameplayPollDelay, cancellationToken: ct);
                }

                // Every SongEntry filed under the same strict hash is byte-identical by
                // definition, so a single representative is enough to hash for all of them.
                // LoadChart() does real file I/O and parsing, so it runs on the thread pool;
                // the continuation (cache + registry writes) resumes on the main thread.
                var representative = kv.Value[0];
                HashWrapper? hashed = await UniTask.RunOnThreadPool(() =>
                {
                    var chart = representative.LoadChart();
                    return chart == null ? (HashWrapper?) null : GameplayHasher.Hash(chart);
                }, cancellationToken: ct);
                if (hashed == null)
                {
                    continue;
                }

                var gameplayHash = hashed.Value;
                GameplayHashCache.Set(strict, gameplayHash.ToString());
                SongContainer.RegisterGameplayHash(kv.Key, gameplayHash);
                sinceLastBatch++;
                anyNewHashes = true;

                await UniTask.Delay(SongDelay, cancellationToken: ct);

                if (sinceLastBatch >= BACKFILL_BATCH_SIZE)
                {
                    await GameplayHashCache.FlushAsync();
                    sinceLastBatch = 0;
                }
            }

            if (sinceLastBatch > 0)
            {
                await GameplayHashCache.FlushAsync();
            }

            if (anyNewHashes)
            {
                BackfillBatchCompleted?.Invoke();
            }

            ToastManager.ToastSuccess("Background scan completed");
        }
    }
}
