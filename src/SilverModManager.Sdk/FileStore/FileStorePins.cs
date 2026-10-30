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
    private static Collection? _collection;

    /// <summary>Pins an xxHash3 <paramref name="identity"/> until the returned scope is disposed. Pins nest: each call counts once.</summary>
    public static IDisposable Pin(FileIdentity identity)
    {
        // The collector keys backups by xxHash3; a pin of another kind would be dropped and the bytes deleted.
        if (identity.Kind != HashKind.XxHash3)
            throw new InvalidOperationException($"A backup is keyed by xxHash3, not {identity}");
        lock (Gate)
        {
            // A collection that has decided what to delete is rewriting archives; a pin taken now would be
            // followed by a HaveFile that still answers from the old index. Wait for the new one.
            while (_collection is { IsSealed: true })
                Monitor.Wait(Gate);

            _collection?.Record(identity);
            Counts[identity] = Counts.GetValueOrDefault(identity) + 1;
        }
        return new Release(identity);
    }

    /// <summary>
    /// Starts watching pins for a collection. Every hash pinned from now until the scope is disposed, plus
    /// every hash pinned when it starts, is reported by <see cref="Collection.Seal"/>, even if the pin is released
    /// first. Only one collection runs at a time.
    /// </summary>
    public static Collection BeginCollection()
    {
        lock (Gate)
        {
            if (_collection is not null)
                throw new InvalidOperationException("A collection is already watching the pins");
            _collection = new Collection(Counts.Keys);
            return _collection;
        }
    }

    /// <summary>The hashes pinned right now.</summary>
    public static IReadOnlyCollection<FileIdentity> Pinned()
    {
        lock (Gate) return Counts.Keys.ToArray();
    }

    /// <summary>
    /// The pins one collection has to honour. A job can pin, find the bytes already stored (the file store's
    /// <c>HaveFile</c> takes no lock), skip the backup, commit its row and unpin, all between the collector
    /// reading the database and deleting. The collector keeps whatever was pinned at any point in that span.
    /// </summary>
    public sealed class Collection : IDisposable
    {
        private readonly HashSet<FileIdentity> _seen;

        internal Collection(IEnumerable<FileIdentity> pinned) => _seen = new HashSet<FileIdentity>(pinned);

        internal bool IsSealed { get; private set; }

        internal void Record(FileIdentity identity) => _seen.Add(identity);

        /// <summary>
        /// Returns every hash pinned since the collection began and makes later <see cref="Pin"/> calls wait
        /// until the collection is disposed, so no pin can land after the set the collector deletes against is final.
        /// </summary>
        public IReadOnlyCollection<FileIdentity> Seal()
        {
            lock (Gate)
            {
                IsSealed = true;
                return _seen.ToArray();
            }
        }

        /// <summary>Ends the collection and releases any <see cref="Pin"/> waiting on it.</summary>
        public void Dispose()
        {
            lock (Gate)
            {
                if (ReferenceEquals(_collection, this)) _collection = null;
                IsSealed = false;
                Monitor.PulseAll(Gate);
            }
        }
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
