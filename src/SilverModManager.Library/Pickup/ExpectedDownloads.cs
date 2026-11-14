using R3;

namespace SilverModManager.Library.Pickup;

/// <inheritdoc />
public sealed class ExpectedDownloads : IExpectedDownloads
{
    private readonly System.Threading.Lock _lock = new();
    private readonly List<ExpectedDownload> _expected = [];
    private readonly Subject<Unit> _changed = new();

    /// <inheritdoc />
    public IReadOnlyList<ExpectedDownload> Current
    {
        get
        {
            lock (_lock) return _expected.ToArray();
        }
    }

    /// <inheritdoc />
    public Observable<Unit> Changed => _changed;

    /// <inheritdoc />
    public IDisposable Expect(ExpectedDownload download)
    {
        lock (_lock) _expected.Add(download);
        _changed.OnNext(Unit.Default);
        return new Registration(this, download);
    }

    private void End(ExpectedDownload download)
    {
        bool removed;
        lock (_lock) removed = _expected.Remove(download);
        if (removed) _changed.OnNext(Unit.Default);
    }

    private sealed class Registration(ExpectedDownloads owner, ExpectedDownload download) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.End(download);
        }
    }
}
