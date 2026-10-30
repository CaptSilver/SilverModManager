using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using RocksDbSharp;

namespace SilverModManager.MnemonicDB.Storage.RocksDbBackend;

internal struct RocksDbIteratorWrapper : ILowLevelIterator, IRefDatomPeekingEnumerator
{
    // The key and value are copied out of RocksDB while the lease is held. A lease only covers a native call, and
    // the pointers RocksDB hands back point into memory the database owns. The caller reads them after the call has
    // returned, which can be after the database was closed. The buffers are pinned so the pointers stay valid.
    private Ptr _key;
    private byte[]? _keyBuffer;
    private byte[]? _valueBuffer;
    private bool _started;
    private readonly Iterator _iterator;
    private readonly Backend _backend;

    public RocksDbIteratorWrapper(Backend backend, Iterator iterator)
    {
        _backend = backend;
        _iterator = iterator;
        _started = false;
    }

    public void Dispose() => 
        _backend.ReleaseIterator(_iterator);

    public unsafe bool MoveNext<TSliceDescriptor>(TSliceDescriptor descriptor, bool useHistory = false) 
        where TSliceDescriptor : ISliceDescriptor, allows ref struct
    {
        using var lease = _backend.BeginUse();
        if (_started == false)
        {
            descriptor.Reset(this, useHistory);
            _started = true;
        }
        else
        {
            descriptor.MoveNext(this);
        }
            
        if (_iterator.Valid())
        {
            var kPtr = Native.Instance.rocksdb_iter_key(_iterator.Handle, out var kLen);
            _key = CopyOut(ref _keyBuffer, (byte*)kPtr, (int)kLen);
            return descriptor.ShouldContinue(_key.Span, useHistory);
        }
        return false;
    }


    public KeyPrefix KeyPrefix => _key.Read<KeyPrefix>(0);
    public Ptr Current => _key;
    public Ptr ValueSpan => _key.SliceFast(KeyPrefix.Size);

    public unsafe Ptr ExtraValueSpan
    {
        get 
        {
            using var lease = _backend.BeginUse();
            var vPtr = Native.Instance.rocksdb_iter_value(_iterator.Handle, out var vLen);
            return CopyOut(ref _valueBuffer, (byte*)vPtr, (int)vLen);
        }
    }

    private static unsafe Ptr CopyOut(ref byte[]? buffer, byte* source, int length)
    {
        if (buffer is null || buffer.Length < length)
            buffer = GC.AllocateUninitializedArray<byte>(Math.Max(length, 256), pinned: true);

        new ReadOnlySpan<byte>(source, length).CopyTo(buffer);
        return new Ptr((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer)), length);
    }

    public bool TryGetRetractionId(out TxId id)
    {
        throw new NotImplementedException();
    }


    public void SeekTo(ReadOnlySpan<byte> span)
    {
        using var lease = _backend.BeginUse();
        _iterator.Seek(span);
    }

    public void SeekToPrev(ReadOnlySpan<byte> span)
    {
        using var lease = _backend.BeginUse();
        _iterator.Seek(span);
        if (!_iterator.Valid())
            _iterator.SeekToLast();
        else
            _iterator.Prev();
    }

    public void Next()
    {
        using var lease = _backend.BeginUse();
        _iterator.Next();
    }

    public void Prev()
    {
        using var lease = _backend.BeginUse();
        _iterator.Prev();
    }
}
