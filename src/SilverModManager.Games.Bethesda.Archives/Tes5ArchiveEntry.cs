// Follows Wabbajack.Compression.BSA/TES5Archive/FileRecord.cs (GPL-3.0).
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using K4os.Compression.LZ4.Streams;
using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

internal sealed class Tes5ArchiveEntry : IBethesdaArchiveEntry
{
    private readonly ArchiveSource _source;
    private readonly Tes5FileState _state;
    private readonly long _payloadPosition;
    private readonly uint _payloadLength;
    private readonly bool _compressed;
    private readonly bool _lz4;

    private Tes5ArchiveEntry(
        ArchiveSource source, Tes5FileState state, long payloadPosition, uint payloadLength, ulong size, bool compressed, bool lz4)
    {
        _source = source;
        _state = state;
        _payloadPosition = payloadPosition;
        _payloadLength = payloadLength;
        Size = size;
        _compressed = compressed;
        _lz4 = lz4;
    }

    public RelativePath Path => _state.Path;

    public ulong Size { get; }

    public FileState State => _state;

    /// <summary>
    /// Reads the entry's small header to learn where the payload starts and how big it is when
    /// decompressed. <paramref name="sizeField"/> is the record's size with the compression flag removed:
    /// it covers the embedded name, the original size of a compressed entry, and the payload.
    /// </summary>
    public static Tes5ArchiveEntry Create(
        ArchiveSource source, Tes5FileState state, uint offset, uint sizeField, bool compressed, bool embeddedNames, bool lz4)
    {
        long position = offset;
        long remaining = sizeField;

        if (embeddedNames)
        {
            var nameLength = source.ReadByte(position);
            position += 1 + nameLength;
            remaining -= 1 + nameLength;
        }

        ulong size;
        if (compressed)
        {
            size = source.ReadUInt32(position);
            position += 4;
            remaining -= 4;
        }
        else
        {
            size = (ulong)Math.Max(remaining, 0);
        }

        if (remaining < 0)
            throw BethesdaArchiveException.Invalid(ArchiveKind.Tes5, offset, $"entry {state.Path} records a size too small for its own header");

        return new Tes5ArchiveEntry(source, state, position, (uint)remaining, size, compressed, lz4);
    }

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        using var _ = await _source.Lock(cancellationToken);
        _source.RequireAvailable(_payloadPosition, _payloadLength);
        _source.Stream.Position = _payloadPosition;

        await using var payload = new BoundedReadStream(_source, _payloadLength);
        if (!_compressed)
        {
            await ArchiveCopy.CopyExactly(payload, destination, Size, ArchiveKind.Tes5, Path.ToString(), cancellationToken);
            return;
        }

        try
        {
            await using Stream decoded = _lz4
                ? LZ4Stream.Decode(payload, leaveOpen: true)
                : new InflaterInputStream(payload) { IsStreamOwner = false };
            await ArchiveCopy.CopyExactly(decoded, destination, Size, ArchiveKind.Tes5, Path.ToString(), cancellationToken);
        }
        catch (Exception e) when (e is SharpZipBaseException or InvalidDataException or EndOfStreamException)
        {
            throw new BethesdaArchiveException($"Tes5 archive entry {Path} could not be decompressed: {e.Message}", e);
        }
    }
}
