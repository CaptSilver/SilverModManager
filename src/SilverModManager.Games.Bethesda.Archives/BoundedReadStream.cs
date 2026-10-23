namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// Exposes <c>length</c> bytes of an archive from its current position, so a decompressor can neither read
/// into the next entry nor close the archive's stream. A stream that ends early is reported as truncation
/// of the archive rather than as a decoder error.
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly ArchiveSource _source;
    private long _remaining;

    public BoundedReadStream(ArchiveSource source, long length)
    {
        _source = source;
        _remaining = length;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining == 0 || buffer.Length == 0) return 0;
        var wanted = (int)Math.Min(buffer.Length, _remaining);
        var offset = _source.Stream.Position;
        var read = _source.Stream.Read(buffer[..wanted]);
        if (read == 0) throw BethesdaArchiveException.Truncated(_source.Kind, offset, _remaining);
        _remaining -= read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
