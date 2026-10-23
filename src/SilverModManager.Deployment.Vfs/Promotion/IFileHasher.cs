using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Hashes a file belonging to an installation. The installation is part of the signature so an
/// implementation that remembers hashes can key them by (installation, path).
/// </summary>
public interface IFileHasher
{
    ValueTask<Hash> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default);
}
