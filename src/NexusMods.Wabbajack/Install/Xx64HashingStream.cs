using System.IO.Hashing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Wabbajack.Install;

/// <summary>
/// Computes the xxHash64 of the bytes that pass through it, as the list records them. Reading from <c>inner</c> hashes
/// what the reader got; writing (with no inner stream) hashes what was written and keeps nothing.
/// </summary>
internal sealed class Xx64HashingStream(Stream? inner = null) : Stream
{
    private readonly XxHash64 _state = Xx64Hasher.Initialize();

    /// <summary>The hash of everything seen so far.</summary>
    public Hash Hash => Xx64Hasher.Finish(_state);

    public override bool CanRead => inner is { CanRead: true };
    public override bool CanSeek => false;
    public override bool CanWrite => inner is null;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = Inner.Read(buffer);
        _state.Append(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await Inner.ReadAsync(buffer, cancellationToken);
        _state.Append(buffer.Span[..read]);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (inner is not null) throw new NotSupportedException();
        _state.Append(buffer);
    }

    private Stream Inner => inner ?? throw new NotSupportedException();

    public override void Flush() { }
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
