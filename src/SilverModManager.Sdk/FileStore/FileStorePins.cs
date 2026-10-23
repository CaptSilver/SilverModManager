using NexusMods.Hashing.xxHash3;

namespace SilverModManager.Sdk.FileStore;

/// <summary>
/// Hashes the garbage collector must keep although no database row references them yet. A job that backs bytes up
/// before it records the row that owns them (a modlist install resolving a game file, say) pins the hash until the
/// row exists, so a collection that runs in between cannot delete what the job is about to read. The state is
/// process-wide because the collector is: it runs under one process-wide lock and reads this set when it marks.
/// </summary>
public static class FileStorePins
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Hash, int> Counts = new();

    /// <summary>Pins <paramref name="hash"/> until the returned scope is disposed. Pins nest: each call counts once.</summary>
    public static IDisposable Pin(Hash hash)
    {
        lock (Gate)
        {
            Counts[hash] = Counts.GetValueOrDefault(hash) + 1;
        }
        return new Release(hash);
    }

    /// <summary>The hashes pinned right now.</summary>
    public static IReadOnlyCollection<Hash> Pinned()
    {
        lock (Gate) return Counts.Keys.ToArray();
    }

    private sealed class Release(Hash hash) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Gate)
            {
                if (--Counts[hash] == 0) Counts.Remove(hash);
            }
        }
    }
}
