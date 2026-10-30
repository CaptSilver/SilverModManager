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
    /// Guards <see cref="_openIterators"/> and <see cref="_closing"/>. Iterators are opened by DuckDB scans on
    /// pool threads, and the native database must not be freed under any of them.
    /// </summary>
    private readonly object _iteratorGate = new();
    private readonly HashSet<IntPtr> _openIterators = new();
    private bool _closing;

    /// <summary>
    /// How long <see cref="Dispose"/> waits for open iterators before it gives up on closing the database.
    /// </summary>
    internal TimeSpan IteratorWait { get; set; } = TimeSpan.FromSeconds(30);

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
        return new Batch(Db!);
    }

    /// <inheritdoc />
    public ISnapshot GetSnapshot()
    {
        lock (_iteratorGate) ObjectDisposedException.ThrowIf(_closing, this);
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
    /// Opens a native iterator and counts it as open until <see cref="ReleaseIterator"/>. Refuses once the backend
    /// is closing, so a query that starts after shutdown fails with an exception instead of reading freed memory.
    /// </summary>
    internal Iterator NewIterator(ReadOptions options)
    {
        lock (_iteratorGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var iterator = Db!.NewIterator(null, options);
            _openIterators.Add(iterator.Handle);
            return iterator;
        }
    }

    /// <summary>
    /// Frees an iterator from <see cref="NewIterator"/>. Safe to call twice for the same iterator.
    /// </summary>
    internal void ReleaseIterator(Iterator iterator)
    {
        var handle = iterator.Handle;
        lock (_iteratorGate)
        {
            if (!_openIterators.Contains(handle)) return;
            iterator.Dispose();
            _openIterators.Remove(handle);
            if (_openIterators.Count == 0) Monitor.PulseAll(_iteratorGate);
        }
    }

    /// <summary>
    /// Flushes all the logs to disk, and performs a compaction, recommended if you want to archive the database
    /// and move it somewhere else.
    /// </summary>
    public void FlushAndCompact(bool verify)
    {
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

        // A query still scanning holds an iterator on the native database, and freeing the database under it
        // crashes the process. New iterators are refused from here on, so the open ones can only drain.
        lock (_iteratorGate)
        {
            _closing = true;
            var deadline = DateTime.UtcNow + IteratorWait;
            while (_openIterators.Count > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                // An iterator that is never released is a leak elsewhere; leaving the database open until the
                // process exits is the safe way to end up with it.
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_iteratorGate, remaining)) return;
            }
        }

        Db?.Dispose();
    }
}
