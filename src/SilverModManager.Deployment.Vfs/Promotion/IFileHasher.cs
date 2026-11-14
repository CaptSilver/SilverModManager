using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Hashes a file belonging to an installation. The installation is part of the signature so an
/// implementation that remembers hashes can key them by (installation, path).
/// </summary>
public interface IFileHasher
{
    ValueTask<FileIdentity> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default);

    /// <summary>
    /// Hashes <paramref name="file"/> below <paramref name="directory"/>, reading through the directory's descriptor.
    /// While a mount covers the folder its path leads back into the mount, so a file that is part of the mount's
    /// backing is only ever read this way.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file is not there.</exception>
    ValueTask<FileIdentity> Hash(GameInstallMetadataId installation, DirectoryHandle directory, RelativePath file, CancellationToken token = default)
        => XxHash3FileHasher.HashThrough(directory, file, token);
}
