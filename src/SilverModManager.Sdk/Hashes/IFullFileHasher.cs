using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Reads a whole game file to hash it. The game indexer only gets here for a file that no stored hash and no previous
/// index entry could vouch for, and the synchronizer for a file whose bytes it is about to archive, so a test counts
/// these calls to see how often the game folder was really read.
/// </summary>
public interface IFullFileHasher
{
    /// <summary>The file's xxHash3.</summary>
    ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken);

    /// <summary>Every digest of the file in one read.</summary>
    ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the file itself, computing every digest in one pass. What a caller gets when nothing replaced the service.
/// </summary>
public sealed class MultiHashFileHasher : IFullFileHasher
{
    /// <summary>The shared instance; the hasher has no state.</summary>
    public static readonly MultiHashFileHasher Instance = new();

    /// <inheritdoc />
    public async ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
        => (await HashAll(file, cancellationToken)).XxHash3;

    /// <inheritdoc />
    public async ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken)
    {
        await using var stream = file.Read();
        return await MultiHasher.HashStream(stream, cancellationToken);
    }
}
