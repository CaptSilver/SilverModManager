using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// An opened archive. Disposing it disposes the stream it was opened over.
/// </summary>
public interface IBethesdaArchiveReader : IAsyncDisposable
{
    ArchiveKind Kind { get; }

    ArchiveState State { get; }

    IReadOnlyList<IBethesdaArchiveEntry> Entries { get; }
}

public interface IBethesdaArchiveEntry
{
    RelativePath Path { get; }

    /// <summary>
    /// The size after decompression.
    /// </summary>
    ulong Size { get; }

    FileState State { get; }

    /// <summary>
    /// Writes the decompressed bytes. Reads on one archive are serialised, so concurrent calls are safe but
    /// do not run in parallel.
    /// </summary>
    Task CopyToAsync(Stream destination, CancellationToken cancellationToken);
}
