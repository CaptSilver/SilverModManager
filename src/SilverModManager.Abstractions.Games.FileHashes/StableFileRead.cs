using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// A game file that kept changing while the app read it, so no hash of it describes it.
/// </summary>
public sealed class GameFileChangedWhileReadException(AbsolutePath file) : UserFacingException(
    $"`{file}` kept changing while it was being read, so the app cannot tell what it holds. Nothing was changed; try again once nothing is writing to it.")
{
    /// <inheritdoc />
    public override string Title => "A game file would not hold still";
}

/// <summary>
/// Reads a whole game file once and vouches that the digests describe it.
/// </summary>
public static class StableFileRead
{
    /// <summary>
    /// Every digest of <paramref name="file"/> and the fingerprint it kept through the read. A file whose size or
    /// modified time moved during the read is read once more; one that moves again is a refusal, because a hash of
    /// mixed bytes would be remembered under a fingerprint that does not describe them.
    /// </summary>
    public static async ValueTask<(MultiHash Hashes, FileFingerprint Fingerprint)> Read(IFullFileHasher hasher, AbsolutePath file, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = FileFingerprint.Of(file);
            var hashes = await hasher.HashAll(file, cancellationToken);
            if (FileFingerprint.Of(file) == before) return (hashes, before);
        }

        throw new GameFileChangedWhileReadException(file);
    }
}
