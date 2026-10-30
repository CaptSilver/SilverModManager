using SilverModManager.Sdk.Hashes;

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
    private static readonly Dictionary<FileIdentity, int> Counts = new();

    /// <summary>Pins an xxHash3 <paramref name="identity"/> until the returned scope is disposed. Pins nest: each call counts once.</summary>
    public static IDisposable Pin(FileIdentity identity)
    {
        // The collector keys backups by xxHash3; a pin of another kind would be dropped and the bytes deleted.
        if (identity.Kind != HashKind.XxHash3)
            throw new InvalidOperationException($"A backup is keyed by xxHash3, not {identity}");
        lock (Gate)
        {
            Counts[identity] = Counts.GetValueOrDefault(identity) + 1;
        }
        return new Release(identity);
    }

    /// <summary>The hashes pinned right now.</summary>
    public static IReadOnlyCollection<FileIdentity> Pinned()
    {
        lock (Gate) return Counts.Keys.ToArray();
    }

    private sealed class Release(FileIdentity identity) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Gate)
            {
                if (--Counts[identity] == 0) Counts.Remove(identity);
            }
        }
    }
}
