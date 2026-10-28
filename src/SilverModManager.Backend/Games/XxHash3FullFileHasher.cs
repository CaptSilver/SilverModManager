using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
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

    public async ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
        => await file.XxHash3Async(token: cancellationToken);

    public async ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken)
    {
        await using var stream = file.Read();
        return await MultiHasher.HashStream(stream, cancellationToken);
    }
}
