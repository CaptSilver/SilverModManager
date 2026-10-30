using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs;

/// <summary>Reads the whole file and hashes it every time.</summary>
public sealed class XxHash3FileHasher : IFileHasher
{
    public async ValueTask<FileIdentity> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default)
        => FileIdentity.XxHash3(await FileHashing.XxHash3(file, token));
}
