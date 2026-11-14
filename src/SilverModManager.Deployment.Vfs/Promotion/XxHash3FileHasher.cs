using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Deployment.Vfs;

/// <summary>Reads the whole file and hashes it every time.</summary>
public sealed class XxHash3FileHasher : IFileHasher
{
    public async ValueTask<FileIdentity> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default)
        => FileIdentity.XxHash3(await FileHashing.XxHash3(file, token));

    public async ValueTask<FileIdentity> Hash(GameInstallMetadataId installation, DirectoryHandle directory, RelativePath file, CancellationToken token = default)
        => await HashThrough(directory, file, token);

    internal static async ValueTask<FileIdentity> HashThrough(DirectoryHandle directory, RelativePath file, CancellationToken token)
        => FileIdentity.XxHash3(await FileHashing.XxHash3(directory.OpenFile(file, FileAccess.Read), token));
}
