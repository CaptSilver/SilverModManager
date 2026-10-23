// Follows Wabbajack.Compression.BSA/BA2Archive/FileEntry.cs (GPL-3.0).
using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

internal sealed class Ba2GeneralEntry(
    ArchiveSource source, Ba2FileState state, long offset, uint packedSize, uint fullSize, bool lz4) : IBethesdaArchiveEntry
{
    public RelativePath Path => state.Path;

    public ulong Size => fullSize;

    public FileState State => state;

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        using var _ = await source.Lock(cancellationToken);
        await Ba2Payload.CopyTo(source, offset, packedSize, fullSize, lz4, destination, Path.ToString(), cancellationToken);
    }
}
