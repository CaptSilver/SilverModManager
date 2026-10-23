using System.Buffers.Binary;

namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// The stream an archive was opened over, with the lock that serialises seeks and reads on it. Every
/// failed read is reported as a <see cref="BethesdaArchiveException"/> carrying the archive kind and the
/// offset it was reading at.
/// </summary>
internal sealed class ArchiveSource : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArchiveSource(Stream stream, ArchiveKind kind)
    {
        Stream = stream;
        Kind = kind;
    }

    public Stream Stream { get; }

    public ArchiveKind Kind { get; }

    public long Length => Stream.Length;

    public async Task<IDisposable> Lock(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    /// <summary>
    /// Fails now, rather than after a huge allocation, when a header claims more bytes than the stream has.
    /// </summary>
    public void RequireAvailable(long offset, ulong count)
    {
        if (offset > Length || count > (ulong)(Length - offset))
            throw BethesdaArchiveException.Truncated(Kind, offset, checked((long)Math.Min(count, long.MaxValue)));
    }

    public byte[] ReadBytes(long offset, int count)
    {
        RequireAvailable(offset, (ulong)count);
        Stream.Position = offset;
        var buffer = new byte[count];
        Stream.ReadExactly(buffer);
        return buffer;
    }

    public byte ReadByte(long offset) => ReadBytes(offset, 1)[0];

    public uint ReadUInt32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(offset, 4));

    public ulong ReadUInt64(long offset) => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(offset, 8));

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return Stream.DisposeAsync();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
