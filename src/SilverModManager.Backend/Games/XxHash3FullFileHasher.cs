using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Backend.Games;

/// <summary>
/// What the game indexer uses when nothing replaced <see cref="IFullFileHasher"/>: the xxHash3 alone is a cheaper read
/// than every digest.
/// </summary>
internal sealed class XxHash3FullFileHasher : IFullFileHasher
{
    public static readonly XxHash3FullFileHasher Instance = new();

    public async ValueTask<FileIdentity> Hash(AbsolutePath file, CancellationToken cancellationToken)
        => FileIdentity.XxHash3(await FileHashing.XxHash3(file, cancellationToken));

    public ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken)
        => MultiHashFileHasher.Instance.HashAll(file, cancellationToken);
}
