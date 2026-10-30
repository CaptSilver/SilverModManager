using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using NexusMods.Paths;
using RocksDbSharp;
using IWriteBatch = SilverModManager.MnemonicDB.Storage.Abstractions.IWriteBatch;

namespace SilverModManager.MnemonicDB.Storage.RocksDbBackend;

/// <summary>
/// Backend for the RocksDB storage engine.
/// </summary>
public class Backend : IStoreBackend
{
    internal RocksDb? Db = null!;
    private IntPtr _comparator;
    private readonly bool _isReadOnly;
    private Env? _env;
    private IntPtr _sliceTransform;
    private volatile bool _disposed;

    /// <summary>
    /// Native calls in progress. DuckDB scans run on pool threads and read through iterators, and the native
    /// database must not be freed while one of those calls is inside it.
    /// </summary>
    private int _activeUses;

    private volatile bool _closing;
    private volatile bool _dbClosed;
    private readonly object _useGate = new();

    /// <summary>
    /// How long <see cref="Dispose"/> waits for native calls in progress before it gives up on closing the database.
    /// </summary>
    internal TimeSpan UseWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Marks a native call on the database or one of its iterators as in progress, for as long as the returned lease
    /// lives. Refused with <see cref="ObjectDisposedException"/> once the backend is closing, so a query that runs
    /// after shutdown fails with an exception instead of reading freed memory.
    /// </summary>
    internal UseLease BeginUse()
    {
        if (_closing) throw new ObjectDisposedException(nameof(Backend));
        Interlocked.Increment(ref _activeUses);
        if (!_closing) return new UseLease(this);

        // Dispose began between the check and the increment.
        EndUse();
        throw new ObjectDisposedException(nameof(Backend));
    }

    private void EndUse()
    {
        if (Interlocked.Decrement(ref _activeUses) != 0 || !_closing) return;
        lock (_useGate) Monitor.PulseAll(_useGate);
    }

    internal readonly struct UseLease(Backend backend) : IDisposable
    {
        public void Dispose() => backend.EndUse();
    }

    /// <summary>
    /// Default constructor
    /// </summary>
    public Backend(bool readOnly = false)
    {
        _disposed = false;
        _isReadOnly = readOnly;
        AttributeCache = new AttributeCache();
    }

    /// <inheritdoc />
    public AttributeCache AttributeCache { get; }

    /// <inheritdoc />
    public IWriteBatch CreateBatch()
    {
        return new Batch(this);
    }

    /// <inheritdoc />
    public ISnapshot GetSnapshot()
    {
        using var _ = BeginUse();
        var snapShot = Db!.CreateSnapshot();
        var readOptions = new ReadOptions()
            .SetTotalOrderSeek(false)
            .SetSnapshot(snapShot)
            .SetPinData(true);
        var snapshot = new Snapshot(this, AttributeCache, readOptions, snapShot);
        return snapshot;
    }

    /// <inheritdoc />
    public void Init(DatomStoreSettings settings)
    {
        _comparator = Native.Instance.rocksdb_comparator_create(IntPtr.Zero, 
            NativeComparators.GetDestructorPtr(),
            NativeComparators.GetNativeFnPtr(),
            NativeComparators.GetNamePtr());

        _sliceTransform = NativePrefixExtractor.MakeSliceTransform();
        
        if (settings.IsInMemory)
            _env = Env.CreateMemEnv();
        else
            _env = Env.CreateDefaultEnv();
        
        var options = new DbOptions()
            .SetCreateIfMissing()
            .SetCreateMissingColumnFamilies()
            .SetCompression(Compression.Zstd)
            .SetComparator(_comparator)
            .SetPrefixExtractor(_sliceTransform)
            .SetEnv(_env.Handle);

        var tableOptions = new BlockBasedTableOptions()
            .SetFilterPolicy(BloomFilterPolicy.Create(10,true));

        options.SetBlockBasedTableFactory(tableOptions);
        
        Native.Instance.rocksdb_options_set_bottommost_compression(options.Handle, (int)Compression.Zstd);

        var nativePath = settings.Path?.ToString() ?? "/dev/in-memory";
        
        if (_isReadOnly)
            Db = RocksDb.OpenReadOnly(options, nativePath, false);
        else 
            Db = RocksDb.Open(options, nativePath);
    }

    /// <summary>
    /// Opens a native iterator. The caller releases it with <see cref="ReleaseIterator"/>.
    /// </summary>
    internal Iterator NewIterator(ReadOptions options)
    {
        using var _ = BeginUse();
        return Db!.NewIterator(null, options);
    }

    /// <summary>
    /// Frees an iterator from <see cref="NewIterator"/>. Once the database is closed the iterator is left alone: it
    /// belongs to a database that no longer exists, and destroying it would run native code on freed memory.
    /// </summary>
    internal void ReleaseIterator(Iterator iterator)
    {
        if (_dbClosed) return;
        UseLease lease;
        try { lease = BeginUse(); }
        catch (ObjectDisposedException) { return; }

        using (lease) iterator.Dispose();
    }

    /// <summary>
    /// Flushes all the logs to disk, and performs a compaction, recommended if you want to archive the database
    /// and move it somewhere else.
    /// </summary>
    public void FlushAndCompact(bool verify)
    {
        using var _ = BeginUse();
        if (verify)
            VerifyOrdering();
        Db?.Flush(new FlushOptions().SetWaitForFlush(true));
        Native.Instance.rocksdb_compact_range(Db!.Handle, IntPtr.Zero, 0, IntPtr.Zero, 0);
        Db?.Flush(new FlushOptions().SetWaitForFlush(true));
    }

    private unsafe void VerifyOrdering()
    {
        using var iterator = Db!.NewIterator();
        iterator.SeekToFirst();

        var prevPtrCache = new PtrCache();
        while (iterator.Valid())
        {
            var ptr = Native.Instance.rocksdb_iter_key(iterator.Handle, out var len);
            var thisPtr = new Ptr((byte*)ptr,(int)len);
            if (!prevPtrCache.IsNull)
            {
                var cmp= GlobalComparer.Compare(thisPtr.Base, thisPtr.Length, prevPtrCache.Ptr.Base, prevPtrCache.Ptr.Length);
                if (cmp <= 0)
                {
                    var prevPrefix = prevPtrCache.Ptr.Read<KeyPrefix>(0);
                    var thisPrefix = thisPtr.Read<KeyPrefix>(0);

                    var sb = new StringBuilder();
                    sb.Append($"Validation failed, ordering is not correct - result: {cmp}");
                    sb.AppendLine($"    this: {thisPrefix} - index: {thisPrefix.Index} - type: {thisPrefix.ValueTag} - length: {thisPtr.Length}");
                    sb.AppendLine($"    prev: {prevPrefix} - index: {prevPrefix.Index} - type: {prevPrefix.ValueTag} - length: {prevPtrCache.Ptr.Length}");
                    
                    throw new Exception(sb.ToString());
                }
            }
            prevPtrCache.CopyFrom(thisPtr);
            iterator.Next();
        } 
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // A scan in the middle of a native call must finish before the database is freed. From here on no new call
        // starts, so the calls in progress can only drain; a scan that resumes later fails with an exception.
        // Iterators nobody released (DuckDB never tells a table function that its query is over) are not waited for.
        lock (_useGate)
        {
            _closing = true;

            // BeginUse increments the count and then checks the flag; this side sets the flag and then reads the
            // count. Without a full fence the flag store can be ordered after the count load, and both sides then
            // see "not yet": the database would be freed under a call that has just started.
            Interlocked.MemoryBarrier();
            var deadline = DateTime.UtcNow + UseWait;
            while (Volatile.Read(ref _activeUses) > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                // A call that never returns is a hang elsewhere; leaving the database open until the process
                // exits is the safe way to end up with it.
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_useGate, remaining)) return;
            }
        }

        _dbClosed = true;
        Db?.Dispose();
    }
}
