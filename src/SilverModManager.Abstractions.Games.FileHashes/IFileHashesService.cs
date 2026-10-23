using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
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
    /// Gets all game files associated with the provided locator IDs.
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
