using System.Collections.Frozen;
using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// Interface for the file hashes service, which provides a way to download and update the file hashes database
/// </summary>
[PublicAPI]
public interface IFileHashesService
{
    /// <summary>
    /// The current file hashes database. While no downloaded or embedded database is connected this is
    /// <see cref="Local"/>, so it always returns something readable.
    /// </summary>
    public IDb Current { get; }

    /// <summary>
    /// The store this app writes itself, from the game's own install data. Always open, and read live:
    /// every access returns the database as of the latest committed transaction.
    /// </summary>
    public IDb Local { get; }

    /// <summary>
    /// Every store a lookup has to consult: the local one first, then the downloaded or embedded database
    /// when one is connected. Entity ids are never compared across these.
    /// </summary>
    public IReadOnlyList<IDb> Databases { get; }

    /// <summary>
    /// Get the file hashes database, downloading it if necessary
    /// </summary>
    public ValueTask<IDb> GetFileHashesDb();

    /// <summary>
    /// Force an update of the file hashes database
    /// </summary>
    /// <param name="forceUpdate">
    /// Fetch the latest release now, even when downloads are turned off in the settings. Without it the
    /// check honours the settings and the update interval.
    /// </param>
    public Task CheckForUpdate(bool forceUpdate = false);

    /// <summary>
    /// Gets all known vanity versions for a given game.
    /// </summary>
    public IEnumerable<VanityVersion> GetKnownVanityVersions(NexusModsGameId nexusModsGameId);

    /// <summary>
    /// Gets the game files of the installation: the depot manifests of its own locator IDs, and the files its
    /// baseline recorded from the manifest without reading them (<see cref="AttestedFiles"/>), which carry a SHA-1
    /// identity. Attested rows belong to the installation's baseline, not to a manifest id, because two installs
    /// can share a manifest id.
    /// </summary>
    public IEnumerable<GameFileRecord> GetGameFiles(GameInstallation installation);

    /// <summary>
    /// Gets all game files the depot manifests of the provided locator IDs list. Manifest rows only: attested
    /// files are found through an installation, see <see cref="GetGameFiles(GameInstallation)"/>.
    /// </summary>
    public IEnumerable<GameFileRecord> GetGameFiles(LocatorIdsWithGameStore locatorIdsWithGameStore);

    /// <summary>
    /// Whether the vanilla file list for these locator ids is the store's own description of every part of the
    /// installed build. False when any id resolves to nothing, so <see cref="GetGameFiles"/> would silently leave
    /// that part out and which paths it holds is unknown. A manifest in the local store counts only when a
    /// baseline built from depot manifests that classified files owns it; a snapshot never does.
    /// </summary>
    public bool CoversInstalledBuild(LocatorIdsWithGameStore locatorIdsWithGameStore);

    /// <summary>
    /// Tries to get a vanity version based on the locator IDs.
    /// </summary>
    public bool TryGetVanityVersion(LocatorIdsWithGameStore locatorIdsWithGameStore, out VanityVersion version);

    /// <summary>
    /// Tries to get all locator IDs for the given store and vanity version.
    /// </summary>
    public bool TryGetLocatorIdsForVanityVersion(GameStore gameStore, VanityVersion version, out LocatorId[] locatorIds);

    /// <summary>
    /// Gets all locator IDs for a given store and <see cref="VersionDefinition"/>.
    /// </summary>
    public LocatorId[] GetLocatorIdsForVersionDefinition(GameStore gameStore, VersionDefinition.ReadOnly versionDefinition);

    /// <summary>
    /// Makes sure the local store holds a vanilla file list for the installation that matches the files
    /// the store (Steam) says are installed, building one when it is missing or stale. A baseline is
    /// rebuilt only when the installation's locator ids changed, or when a depot manifest that was
    /// missing at the last build has been cached since.
    /// </summary>
    public Task<LocalBaselineResult> EnsureLocalBaseline(GameInstallation installation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-keys the baseline at <paramref name="oldPath"/> to <paramref name="newPath"/>, for a managed game the
    /// registry followed to another folder: the baseline was taken before mods were deployed, and no other
    /// can be built from the folder as it is now. A baseline from complete depot manifests already at the new
    /// path stays and the old one is deleted. Nothing happens when there is none at the old path.
    /// </summary>
    public Task MoveLocalBaseline(GameStore store, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the local baseline of the installation at <paramref name="gamePath"/>, with the rows only it
    /// uses. Nothing happens when there is none. For a game that moved: its baseline at the old folder is not
    /// read by anything any more.
    /// </summary>
    public Task RemoveLocalBaseline(GameStore store, string gamePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes once a local baseline build for the installation that is already running has committed its
    /// rows, or at once when none is running. Never starts a build. A build commits all of its rows at the
    /// end, so anything that looks hashes up while one runs sees none of them and reads every file instead.
    /// </summary>
    public Task WaitForLocalBaselineBuild(GameInstallation installation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers the xxHash3 of files whose hash the index had to produce, replacing the rows of the same location
    /// and path, in one transaction on <see cref="Local"/>. Those are files read in full and found unchanged by a
    /// second stat, and files whose hash came from a stored <see cref="PathHashRelation"/>. A hash the index took
    /// from the file's own fingerprint row is not passed in again. Observes the token before it writes.
    /// </summary>
    public Task RecordIndexedFiles(GameInstallation installation, IReadOnlyList<IndexedFileHash> files, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives the hash recorded by <see cref="RecordIndexedFiles"/> for this exact location, path, size and modified time.
    /// Accepts a row only for a file the app itself read and re-stat'ed unchanged, found by location and by the
    /// ordinal spelling of the path. It still accepts a same-size rewrite that restored the modified time, and on
    /// FAT/exFAT libraries (2 s timestamps) such a rewrite is easier to produce; the SHA-1 check of an attested
    /// file and the full read after any other change are what close those.
    /// </summary>
    public bool TryGetFingerprintedHash(GameInstallation installation, GamePath path, Size size, DateTimeOffset modifiedUtc, out Hash xxHash3);

    /// <summary>
    /// The files the store vouches for that the installation's baseline recorded from the manifest without reading
    /// them, keyed by their path in the game folder. Empty when the installation has no baseline.
    /// <para>
    /// A row is the store's description of the path, confirmed by the size and modified time the file had when the
    /// row was written. A same-size replacement that kept the modified time, made before the app first indexed the
    /// path, is therefore taken for the vanilla file until something reads it (a mod that covers the file, a merge,
    /// a recognizer). That is accepted: reading every vanilla file to rule it out is what these rows exist to avoid.
    /// </para>
    /// </summary>
    public FrozenDictionary<GamePath, AttestedLookup> AttestedFiles(GameInstallation installation);

    /// <summary>
    /// Records what reading attested files found, in one transaction on <see cref="Local"/>. A file whose SHA-1 and
    /// size match its row is promoted: the hashes become a <see cref="HashRelation"/> with a
    /// <see cref="PathHashRelation"/> in the baseline and the manifest, and the row is deleted. One that does not
    /// match keeps its row, gets a <see cref="HashRelation"/> for the bytes read and is logged as an external change.
    /// Every item also gets a <see cref="LocalFileHash"/> row for <c>After</c>, the fingerprint taken after the
    /// read. A row that is gone (a rebuild replaced it) still gets the fingerprint row and the relation, so the new
    /// baseline resolves the path. The caller drops an item whose file changed while it was read.
    /// </summary>
    public Task SettleAttested(GameInstallation installation, IReadOnlyList<(AttestedLookup Attested, MultiHash Hashes, FileFingerprint After)> read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suggest version data for a given game installation and files.
    /// </summary>
    public Optional<VersionData> SuggestVersionData(GameInstallation gameInstallation, IEnumerable<(GamePath Path, Hash Hash)> files);
}

/// <summary>
/// What <see cref="IFileHashesService.EnsureLocalBaseline"/> found or built.
/// </summary>
/// <param name="Kind">Where the baseline's file list came from.</param>
/// <param name="Rebuilt">False when the existing baseline was kept, or when nothing was built because the store is mid-update.</param>
/// <param name="NonVanillaCount">Files that differ from, or are missing in, the depot manifests.</param>
public sealed record LocalBaselineResult(LocalBaselineKind Kind, bool Rebuilt, int NonVanillaCount);

/// <summary>
/// Tuple of many <see cref="LocatorId"/> and <see cref="Sdk.VanityVersion"/>.
/// </summary>
public record struct VersionData(LocatorId[] LocatorIds, VanityVersion VanityVersion);
