using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Song;

namespace YARG.Online
{
    /// <summary>
    /// Builds a <see cref="SongLibraryDto"/> from the local player's installed
    /// songs. The server uses it to compute the lobby-wide shared library
    /// (intersection of every member's library).
    /// </summary>
    internal static class LocalSongLibrary
    {
        public static SongLibraryDto BuildLocal()
        {
            var byHash = SongContainer.SongsByHash;
            var hashes = new string[byHash.Count];
            int i = 0;
            foreach (var kv in byHash)
            {
                hashes[i++] = kv.Key.ToString();
            }
            return new SongLibraryDto(hashes);
        }
    }
}
