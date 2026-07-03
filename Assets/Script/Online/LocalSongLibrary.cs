using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// Snapshots the local player's installed song hashes. The snapshot is streamed to the
    /// lobby hub (see <see cref="LobbyHubSession"/>) so the server can compute the lobby-wide
    /// shared library (intersection of every member's library).
    /// </summary>
    internal static class LocalSongLibrary
    {
        /// <summary>
        /// Materialize every installed song hash on the calling thread. Must be called from
        /// the main thread -- the resulting array is what gets chunked and streamed, so the
        /// SignalR send thread never touches <see cref="SongContainer.SongsByHash"/>.
        /// </summary>
        public static string[] SnapshotLocalHashes()
        {
            var byHash = SongContainer.SongsByHash;
            var hashes = new string[byHash.Count];
            int i = 0;
            foreach (var kv in byHash)
            {
                hashes[i++] = kv.Key.ToString();
            }
            return hashes;
        }
    }
}
