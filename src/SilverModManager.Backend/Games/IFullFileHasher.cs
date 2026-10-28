using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Backend.Games;

/// <summary>
/// Reads a whole file to hash it. <see cref="GameLocationsService"/> only gets here for a file that no stored
/// hash and no previous index entry could vouch for, so a test counts these calls to see how often the
/// game folder was really read.
/// </summary>
internal interface IFullFileHasher
{
    /// <summary>The file's xxHash3.</summary>
    ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken);

    /// <summary>Every digest of the file in one read.</summary>
    ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken);
}

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
