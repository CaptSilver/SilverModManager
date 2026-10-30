namespace SilverModManager.Wabbajack.Acquire;

/// <summary>Passes every read through to <paramref name="inner"/> and tells <paramref name="onRead"/> how many bytes it returned.</summary>
internal sealed class ReadCountingStream(Stream inner, Action<int> onRead) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Counted(await inner.ReadAsync(buffer, cancellationToken));

    private int Counted(int read)
    {
        if (read > 0) onRead(read);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
