using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Deployment;

/// <summary>
/// Loose, content-addressed copies of archived files on disk, kept per managed game. Backends that
/// deploy by linking or mounting read their file contents from here instead of extracting them into
/// the game folder.
/// </summary>
/// <remarks>
/// A file lives at <c>&lt;Root&gt;/&lt;installation&gt;/&lt;hh&gt;/&lt;hash&gt;</c>, where the installation directory is the
/// entity id in lowercase hex, <c>hash</c> is 16 lowercase hex digits and <c>hh</c> its first two.
/// Directories under <see cref="Root"/> that do not parse as an installation are ignored.
/// </remarks>
public interface ILooseStagingStore
{
    /// <summary>
    /// The directory all staged files live under. It may not exist yet.
    /// </summary>
    AbsolutePath Root { get; }

    /// <summary>
    /// Where the file with this hash is, or would be, staged for the installation.
    /// </summary>
    AbsolutePath PathFor(GameInstallMetadataId installation, Hash hash);

    /// <summary>
    /// Makes sure the file is staged, extracting it from the file store if it is not, and returns its path.
    /// A file that is already staged is not extracted again. A crash while extracting never leaves
    /// a partial file under the final name.
    /// </summary>
    ValueTask<AbsolutePath> Ensure(GameInstallMetadataId installation, Hash hash, CancellationToken token = default);

    /// <summary>
    /// Whether the file is staged for the installation.
    /// </summary>
    bool Contains(GameInstallMetadataId installation, Hash hash);

    /// <summary>
    /// Every file staged for the installation. Empty when nothing was ever staged for it.
    /// </summary>
    IEnumerable<(Hash Hash, AbsolutePath Path)> Enumerate(GameInstallMetadataId installation);

    /// <summary>
    /// The installations that have a staging directory.
    /// </summary>
    IEnumerable<GameInstallMetadataId> Installations();

    /// <summary>
    /// Deletes every staged file that is not in <paramref name="live"/> for its installation. An
    /// installation that is missing from <paramref name="live"/> loses its whole directory. Does nothing,
    /// and creates nothing, when <see cref="Root"/> does not exist.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    int Sweep(IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>> live);
}
