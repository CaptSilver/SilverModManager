using NexusMods.Sdk.Library;

namespace NexusMods.Abstractions.Library;

/// <summary>
/// Adds an asset of a GitHub release to the library, verified. It exists so a game project can ask for a release
/// without referencing the project that downloads one.
/// </summary>
public interface IGitHubReleaseAssetSource
{
    /// <summary>
    /// Downloads the asset of the release GitHub marks as latest in the repository with the given id and adds it to
    /// the library. The asset is the one whose name ends with <paramref name="suffix"/> (preferring one that contains
    /// <paramref name="nameHint"/>), and its bytes must match the SHA-256 GitHub reports for it: an asset without a
    /// reported digest is refused. Throws when the release, the asset or the digest is not right; nothing is added then.
    /// </summary>
    ValueTask<LibraryFile.ReadOnly> AddLatestAssetAsync(ulong repositoryId, string suffix, string? nameHint, CancellationToken cancellationToken);
}
