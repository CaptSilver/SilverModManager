using DynamicData.Kernel;
using JetBrains.Annotations;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Abstractions.Games.FileHashes;

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
    /// Suggest version data for a given game installation and files.
    /// </summary>
    public Optional<VersionData> SuggestVersionData(GameInstallation gameInstallation, IEnumerable<(GamePath Path, Hash Hash)> files);
}

/// <summary>
/// Tuple of many <see cref="LocatorId"/> and <see cref="Sdk.VanityVersion"/>.
/// </summary>
public record struct VersionData(LocatorId[] LocatorIds, VanityVersion VanityVersion);
