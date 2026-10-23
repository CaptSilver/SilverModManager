namespace NexusMods.Wabbajack.Transforms;

/// <summary>
/// Forwards writes to <paramref name="inner"/> and throws <see cref="OperationCanceledException"/>
/// once the token is cancelled. Lets synchronous code without a token be stopped at its next write.
/// Reads and seeks pass through: Octodiff hashes the finished output by reading it back.
/// </summary>
internal sealed class CancellableWriteStream(Stream inner, CancellationToken token) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => true;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        token.ThrowIfCancellationRequested();
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        token.ThrowIfCancellationRequested();
        inner.Write(buffer);
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
}
