using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using K4os.Compression.LZ4;

namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// Reads one stored or compressed run of bytes out of a BA2: a general file's data or one texture chunk.
/// </summary>
internal static class Ba2Payload
{
    // LZ4 cannot expand a block by more than this factor, so a larger claimed size is a corrupt header and
    // not worth allocating for.
    private const ulong MaxLz4Expansion = 255;

    /// <summary>
    /// Writes <paramref name="fullSize"/> bytes. <paramref name="packedSize"/> is zero for stored data. The
    /// caller holds the archive's lock.
    /// </summary>
    public static async Task CopyTo(
        ArchiveSource source,
        long position,
        uint packedSize,
        uint fullSize,
        bool lz4,
        Stream destination,
        string what,
        CancellationToken cancellationToken)
    {
        if (packedSize == 0)
        {
            source.RequireAvailable(position, fullSize);
            source.Stream.Position = position;
            await using var stored = new BoundedReadStream(source, fullSize);
            await ArchiveCopy.CopyExactly(stored, destination, fullSize, ArchiveKind.Ba2, what, cancellationToken);
            return;
        }

        source.RequireAvailable(position, packedSize);
        try
        {
            if (lz4)
            {
                if (fullSize > packedSize * MaxLz4Expansion)
                    throw BethesdaArchiveException.Invalid(ArchiveKind.Ba2, position, $"{what} claims {fullSize} bytes from {packedSize} compressed bytes");

                var packed = source.ReadBytes(position, checked((int)packedSize));
                var full = new byte[fullSize];
                var decoded = LZ4Codec.PartialDecode(packed, full);
                if (decoded != fullSize)
                    throw new BethesdaArchiveException($"Ba2 archive entry {what} could not be decompressed: the LZ4 block holds {decoded} of {fullSize} bytes");
                await destination.WriteAsync(full, cancellationToken);
                return;
            }

            source.Stream.Position = position;
            await using var payload = new BoundedReadStream(source, packedSize);
            await using var inflated = new InflaterInputStream(payload) { IsStreamOwner = false };
            await ArchiveCopy.CopyExactly(inflated, destination, fullSize, ArchiveKind.Ba2, what, cancellationToken);
        }
        catch (Exception e) when (e is SharpZipBaseException or InvalidDataException or EndOfStreamException)
        {
            throw new BethesdaArchiveException($"Ba2 archive entry {what} could not be decompressed: {e.Message}", e);
        }
    }
}
