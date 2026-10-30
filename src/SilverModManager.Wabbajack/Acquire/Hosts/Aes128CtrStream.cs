using System.Numerics;
using System.Security.Cryptography;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Reads a stream and decrypts (or encrypts, the operation is its own inverse) it with AES-128 in counter mode. .NET
/// has no CTR mode, so the keystream is built by encrypting counter blocks in ECB mode: a block is the 8-byte nonce
/// followed by the block number as a big-endian integer. The keystream position follows the bytes read, so reads
/// need not be a multiple of 16.
/// </summary>
public sealed class Aes128CtrStream : Stream
{
    private const int BlocksPerBatch = 4096;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly Aes _aes = Aes.Create();
    private readonly byte[] _nonce;
    private readonly byte[] _counters = new byte[BlocksPerBatch * 16];
    private readonly byte[] _keystream = new byte[BlocksPerBatch * 16];
    private readonly ulong _firstBlock;
    private long _position;

    public Aes128CtrStream(Stream inner, byte[] key, byte[] nonce, ulong firstBlock = 0, bool leaveOpen = false)
    {
        if (key.Length != 16) throw new ArgumentException("An AES-128 key is 16 bytes", nameof(key));
        if (nonce.Length != 8) throw new ArgumentException("The nonce is 8 bytes", nameof(nonce));
        _inner = inner;
        _leaveOpen = leaveOpen;
        _nonce = nonce.ToArray();
        _firstBlock = firstBlock;
        _aes.Key = key;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Apply(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        Apply(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Apply(Span<byte> data)
    {
        while (data.Length > 0)
        {
            var block = (ulong)(_position / 16);
            var within = (int)(_position % 16);
            var blocks = Math.Min(BlocksPerBatch, (within + data.Length + 15) / 16);

            for (var i = 0; i < blocks; i++)
            {
                var counter = _counters.AsSpan(i * 16, 16);
                _nonce.CopyTo(counter);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counter[8..], _firstBlock + block + (ulong)i);
            }
            _aes.EncryptEcb(_counters.AsSpan(0, blocks * 16), _keystream, PaddingMode.None);

            var take = Math.Min(data.Length, blocks * 16 - within);
            Xor(data[..take], _keystream.AsSpan(within, take));
            data = data[take..];
            _position += take;
        }
    }

    private static void Xor(Span<byte> data, ReadOnlySpan<byte> key)
    {
        var i = 0;
        for (; i <= data.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            (new Vector<byte>(data[i..]) ^ new Vector<byte>(key[i..])).CopyTo(data[i..]);
        for (; i < data.Length; i++) data[i] ^= key[i];
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            if (!_leaveOpen) _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _aes.Dispose();
        if (!_leaveOpen) await _inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
