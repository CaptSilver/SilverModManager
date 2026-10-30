using System;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Internals;
using RocksDbSharp;

namespace SilverModManager.MnemonicDB.Storage.RocksDbBackend;

internal struct RocksDbIteratorWrapper : ILowLevelIterator, IRefDatomPeekingEnumerator
{
    private Ptr _key;
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
            _key = new Ptr((byte*)kPtr, (int)kLen);
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
            return new Ptr((byte*)vPtr, (int)vLen);
        }
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
