using SilverModManager.Sdk.FileStore;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Collects the files a resolver wants kept and hands them to the store a batch at a time. A backup is a file in the
/// store, a read of its whole index and an index rebuild, whatever it holds; one per archive member made an install
/// of a large list spend most of its time and memory there. A batch goes when it holds <paramref name="maxEntries"/>
/// files or <paramref name="maxBytes"/> bytes, or when <paramref name="maxWait"/> has passed since its first file.
/// </summary>
internal sealed class BackupBatcher(IFileStore store, CancellationToken token, int maxEntries = 64, long maxBytes = 128L * 1024 * 1024, TimeSpan? maxWait = null)
{
    private readonly TimeSpan _maxWait = maxWait ?? TimeSpan.FromMilliseconds(25);
    private readonly object _lock = new();
    private List<(ArchivedFileEntry Entry, TaskCompletionSource Done)> _pending = [];
    private long _pendingBytes;

    /// <summary>
    /// Completes when <paramref name="entry"/> is in the store, or throws what the store threw for its batch. The
    /// entry's stream must stay open until then.
    /// </summary>
    public Task Add(ArchivedFileEntry entry)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<(ArchivedFileEntry, TaskCompletionSource)>? full = null;
        var first = false;
        lock (_lock)
        {
            first = _pending.Count == 0;
            _pending.Add((entry, done));
            _pendingBytes += (long)entry.Size.Value;
            if (_pending.Count >= maxEntries || _pendingBytes >= maxBytes) full = TakePending();
        }

        if (full is not null) _ = Send(full);
        else if (first) _ = FlushAfterWait();
        return done.Task;
    }

    private List<(ArchivedFileEntry Entry, TaskCompletionSource Done)> TakePending()
    {
        var batch = _pending;
        _pending = [];
        _pendingBytes = 0;
        return batch;
    }

    private async Task FlushAfterWait()
    {
        await Task.Delay(_maxWait, CancellationToken.None);
        List<(ArchivedFileEntry Entry, TaskCompletionSource Done)> batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = TakePending();
        }
        await Send(batch);
    }

    private async Task Send(List<(ArchivedFileEntry Entry, TaskCompletionSource Done)> batch)
    {
        try
        {
            // Pins are the caller's: each is taken before its file is added here.
            await store.BackupFiles(batch.Select(b => b.Entry).ToArray(), deduplicate: true, token);
            foreach (var (_, done) in batch) done.TrySetResult();
        }
        catch (Exception e)
        {
            foreach (var (_, done) in batch) done.TrySetException(e);
        }
    }
}
