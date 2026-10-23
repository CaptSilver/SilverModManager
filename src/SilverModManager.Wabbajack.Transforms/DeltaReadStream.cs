namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Read-only view of a delta that fails loudly where Octodiff would not. Its data-command loop
/// reads until a byte count reaches zero and treats a short read as progress, so a delta cut off
/// mid-command makes it spin forever on end-of-stream reads. This turns that read into an error,
/// and gives the apply a place to notice a cancelled token between commands.
/// </summary>
internal sealed class DeltaReadStream(Stream inner, CancellationToken token) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        token.ThrowIfCancellationRequested();
        var read = inner.Read(buffer, offset, count);
        if (read == 0 && count > 0) throw new EndOfStreamException("The delta ends before its last command is complete.");
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
