namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// The few <see cref="INestedArchive"/>s a resolver has open at once, by the key of the container they were opened
/// from. A list reads tens of thousands of members out of far fewer archives, and opening one costs reading its index.
/// An archive that is being read is never let go; beyond <paramref name="capacity"/> the one used longest ago that
/// nothing is reading is disposed.
/// </summary>
internal sealed class NestedArchiveCache(int capacity) : IAsyncDisposable
{
    private readonly Dictionary<string, Slot> _slots = [];
    private long _clock;
    private bool _disposed;

    internal sealed class Slot(Lazy<Task<INestedArchive>> archive)
    {
        public Lazy<Task<INestedArchive>> Archive { get; } = archive;
        public int Readers;
        public long LastUsed;
    }

    /// <summary>A hold on an archive; the cache does not dispose it until this is disposed.</summary>
    public sealed class Lease : IAsyncDisposable
    {
        private readonly NestedArchiveCache _owner;
        private readonly string _key;
        private readonly Slot _slot;
        private int _released;

        internal Lease(NestedArchiveCache owner, string key, Slot slot, INestedArchive archive)
        {
            _owner = owner;
            _key = key;
            _slot = slot;
            Archive = archive;
        }

        public INestedArchive Archive { get; }

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? _owner.Release(_key, _slot) : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The archive for <paramref name="key"/>, opened with <paramref name="open"/> unless it already is. Callers that
    /// ask for one key together wait for the one open.
    /// </summary>
    public async Task<Lease> Acquire(string key, Func<Task<INestedArchive>> open)
    {
        Slot slot;
        lock (_slots)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_slots.TryGetValue(key, out var existing)) slot = existing;
            else
            {
                slot = new Slot(new Lazy<Task<INestedArchive>>(open));
                _slots[key] = slot;
            }
            slot.Readers++;
            slot.LastUsed = ++_clock;
        }

        INestedArchive archive;
        try
        {
            archive = await slot.Archive.Value;
        }
        catch
        {
            // A failed open is not remembered: the next ask tries again.
            lock (_slots)
            {
                slot.Readers--;
                if (_slots.TryGetValue(key, out var current) && ReferenceEquals(current, slot)) _slots.Remove(key);
            }
            throw;
        }
        return new Lease(this, key, slot, archive);
    }

    private async ValueTask Release(string key, Slot slot)
    {
        var evicted = new List<Slot>();
        lock (_slots)
        {
            slot.Readers--;
            slot.LastUsed = ++_clock;
            if (_disposed)
            {
                if (slot.Readers == 0 && _slots.Remove(key)) evicted.Add(slot);
            }
            else
            {
                while (_slots.Count > capacity)
                {
                    var idle = _slots.Where(pair => pair.Value.Readers == 0).OrderBy(pair => pair.Value.LastUsed).FirstOrDefault();
                    if (idle.Value is null) break;
                    _slots.Remove(idle.Key);
                    evicted.Add(idle.Value);
                }
            }
        }
        await DisposeAll(evicted);
    }

    /// <summary>Disposes the archives nothing is reading now, and each of the others as its last reader lets go.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Slot> idle;
        lock (_slots)
        {
            _disposed = true;
            idle = _slots.Where(pair => pair.Value.Readers == 0).Select(pair => pair.Value).ToList();
            foreach (var pair in _slots.Where(pair => pair.Value.Readers == 0).ToList()) _slots.Remove(pair.Key);
        }
        await DisposeAll(idle);
    }

    private static async ValueTask DisposeAll(IEnumerable<Slot> slots)
    {
        foreach (var slot in slots)
        {
            // Only a slot whose open finished holds anything to dispose; one that failed was removed already.
            if (slot.Archive.IsValueCreated && slot.Archive.Value.IsCompletedSuccessfully)
                await slot.Archive.Value.Result.DisposeAsync();
        }
    }
}
