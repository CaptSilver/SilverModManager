using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SilverModManager.MnemonicDB.Abstractions;
using WriteState = SilverModManager.Wabbajack.Install.InstallModlistJob.WriteState;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// The rows and generated files of the commit batch an install is filling: the open transaction, how much has gone into
/// it, and the generated files waiting for the file store. Rows are written by one thread, files are queued and flushed
/// from the workers, so the pending list and the flush are guarded separately from the counters.
/// </summary>
internal sealed class WriteBatch(IConnection connection, CommitPolicy policy, GeneratedFileWriter writer)
{
    private readonly object _pendingLock = new();
    private List<GeneratedFile> _pending = new();
    private long _pendingBytes;

    // Held while pending files are handed to the file store, so a flush never starts before an earlier one has finished.
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    // Set, under _flushLock, when handing files to the file store failed. A failed flush has already taken files of items
    // that finished, so a later flush would find nothing waiting and let their rows commit.
    private ExceptionDispatchInfo? _flushFailure;

    private IMainTransaction? _tx;
    private readonly Stopwatch _age = new();

    public int Items { get; set; }
    public long Bytes { get; set; }

    /// <summary>Whether the open batch holds rows, so a commit has something to write.</summary>
    public bool Dirty { get; set; }

    public bool IsDue => Items >= policy.MaxItems || Bytes >= policy.MaxBytes || _age.Elapsed >= policy.MaxInterval;

    /// <summary>The batch's transaction, begun on first use; its age counts from then.</summary>
    public IMainTransaction Open()
    {
        if (_tx is { } open) return open;
        _age.Restart();
        return _tx = connection.BeginTransaction();
    }

    /// <summary>Queues a generated file for the file store and returns how many are waiting and their bytes.</summary>
    public (int Count, long Bytes) AddPending(GeneratedFile file)
    {
        lock (_pendingLock)
        {
            _pending.Add(file);
            _pendingBytes += (long)file.Size.Value;
            return (_pending.Count, _pendingBytes);
        }
    }

    /// <summary>Hands the waiting generated files to the file store, or throws the failure of an earlier flush.</summary>
    public async Task Flush(CancellationToken cancellationToken)
    {
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            _flushFailure?.Throw();
            var waiting = TakePending();
            if (waiting.Count == 0) return;
            try
            {
                await writer.Store(waiting, cancellationToken);
            }
            catch (Exception e)
            {
                _flushFailure = ExceptionDispatchInfo.Capture(e);
                throw;
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>
    /// Stores the generated files the open batch refers to, commits its rows and starts the next batch. Files first, so a
    /// row never names bytes the store lacks. The groups the batch created get their committed ids in <paramref name="state"/>.
    /// </summary>
    public async Task Commit(WriteState state, CancellationToken cancellationToken)
    {
        if (_tx is not { } tx) return;

        if (Dirty)
        {
            await Flush(cancellationToken);
            var result = await tx.Commit();
            foreach (var name in state.NewGroups) state.Groups[name] = result[state.Groups[name]];
            state.NewGroups.Clear();
        }

        tx.Dispose();
        _tx = null;
        Items = 0;
        Bytes = 0;
        Dirty = false;
        _age.Reset();
    }

    /// <summary>Drops the open transaction and deletes the temp files of generated files an install that stopped early never stored.</summary>
    public async Task Discard()
    {
        _tx?.Dispose();
        _tx = null;
        foreach (var file in TakePending()) await file.DisposeAsync();
    }

    private List<GeneratedFile> TakePending()
    {
        lock (_pendingLock)
        {
            var taken = _pending;
            _pending = new List<GeneratedFile>();
            _pendingBytes = 0;
            return taken;
        }
    }
}
