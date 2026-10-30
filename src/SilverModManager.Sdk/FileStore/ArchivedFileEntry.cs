using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Sdk.FileStore;

/// <summary>
/// A helper class for <see cref="IFileStore"/> that represents a file to be backed up. The Path is optional,
/// but should be provided if it is expected that the paths will be used for extraction or mod installation.
/// </summary>
public readonly record struct ArchivedFileEntry
{
    /// <param name="streamFactory">Opens the bytes to back up.</param>
    /// <param name="identity">The xxHash3 identity of the bytes: the store is keyed by it, so any other kind is refused.</param>
    /// <param name="size">The length of the bytes.</param>
    /// <exception cref="InvalidOperationException"><paramref name="identity"/> is not an xxHash3.</exception>
    public ArchivedFileEntry(IStreamFactory streamFactory, FileIdentity identity, Size size)
    {
        if (identity.Kind != HashKind.XxHash3)
            throw new InvalidOperationException($"A backup is keyed by xxHash3, not {identity}");
        StreamFactory = streamFactory;
        Identity = identity;
        Size = size;
    }

    /// <summary>Opens the bytes to back up.</summary>
    public IStreamFactory StreamFactory { get; }

    /// <summary>The xxHash3 identity of the bytes.</summary>
    public FileIdentity Identity { get; }

    /// <summary>The length of the bytes.</summary>
    public Size Size { get; }
}
