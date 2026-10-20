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
    /// The directory the installation's staged files live in, created if it does not exist. The link
    /// backends write their probe files here so the probe sees the same filesystem as the staged files.
    /// </summary>
    AbsolutePath DirectoryFor(GameInstallMetadataId installation);

    /// <summary>
    /// Unlinks the staged file for the installation and returns whether one was there. Only the store's
    /// own path goes: a link made from it into a game folder keeps the bytes alive and is not touched.
    /// The next <see cref="Ensure"/> extracts the file again.
    /// </summary>
    bool Release(GameInstallMetadataId installation, Hash hash);

    /// <summary>
    /// Stops <see cref="Sweep"/> from running until the returned handle is disposed. Holds are shared and
    /// reentrant. A hold taken while a sweep runs waits for the sweep to finish. An apply holds one from
    /// before it probes the store directory until its links are recorded, because a sweep in between
    /// cannot yet see the files that apply is staging.
    /// </summary>
    IDisposable Hold();

    /// <summary>
    /// Deletes every staged file that is not in the set <paramref name="live"/> returns for its
    /// installation, together with stale temp and probe files left in the store. An installation that is
    /// missing from the result loses its whole directory. Waits for every <see cref="Hold"/> first, and
    /// calls <paramref name="live"/> only after that, so the set reflects everything those holders
    /// committed. Does nothing, and creates nothing, when <see cref="Root"/> does not exist.
    /// </summary>
    /// <returns>The number of staged files deleted.</returns>
    int Sweep(Func<IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>>> live);
}
