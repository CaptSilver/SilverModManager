using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.FileStore;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// Collects the files a resolver wants kept and hands them to the store a batch at a time. A backup is a file in the
/// store, a read of its whole index and an index rebuild, whatever it holds; one per archive member made an install
/// of a large list spend most of its time and memory there. A batch goes when it holds <paramref name="maxEntries"/>
/// files or <paramref name="maxBytes"/> bytes, or when <paramref name="maxWait"/> has passed since its first file.
/// </summary>
internal sealed class BackupBatcher(IFileStore store, CancellationToken token, int maxEntries = 64, long maxBytes = 128L * 1024 * 1024, TimeSpan? maxWait = null, ILogger? logger = null)
{
    private readonly TimeSpan _maxWait = maxWait ?? TimeSpan.FromMilliseconds(25);
    private readonly object _lock = new();
    private List<(ArchivedFileEntry Entry, TaskCompletionSource Done)> _pending = [];
    private long _pendingBytes;
    private int _inFlight;

    /// <summary>How many batches are being stored right now; a store that is slow to take them shows up as a pile of these.</summary>
    internal int InFlight => Volatile.Read(ref _inFlight);

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
        var inFlight = Interlocked.Increment(ref _inFlight);
        logger?.LogDebug("Storing a batch of {Count} files ({Megabytes:F1} MB); {InFlight} batches in flight",
            batch.Count, batch.Sum(b => (double)b.Entry.Size.Value) / (1024 * 1024), inFlight);
        try
        {
            await Store(batch);
        }
        catch (Exception e) when (batch.Count > 1 && e is not OperationCanceledException && !token.IsCancellationRequested)
        {
            // One unreadable file must not fail the up to 64 unrelated members that happened to share its batch, so
            // each goes again by itself; the one at fault fails again and the rest are stored. A failure that is
            // not the file's (a full disk) fails each of them in turn, as it would have without batching.
            foreach (var single in batch) await StoreAlone(single);
        }
        catch (Exception e)
        {
            foreach (var (_, done) in batch) done.TrySetException(e);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task Store(List<(ArchivedFileEntry Entry, TaskCompletionSource Done)> batch)
    {
        // Pins are the caller's: each is taken before its file is added here.
        await store.BackupFiles(batch.Select(b => b.Entry).ToArray(), deduplicate: true, token);
        foreach (var (_, done) in batch) done.TrySetResult();
    }

    private async Task StoreAlone((ArchivedFileEntry Entry, TaskCompletionSource Done) single)
    {
        try
        {
            await Store([single]);
        }
        catch (Exception e)
        {
            single.Done.TrySetException(e);
        }
    }
}
