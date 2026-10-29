using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Sdk.Games;

[PublicAPI]
public interface IGameRegistry
{
    /// <summary>
    /// Locates all games installed on the system.
    /// </summary>
    ImmutableArray<GameInstallation> LocateGameInstallations();

    /// <summary>
    /// What the locators reported during the latest locate. <see cref="ClearCache"/> keeps them; the next locate
    /// replaces them.
    /// </summary>
    ImmutableArray<GameLocatorProblem> Problems { get; }

    /// <summary>
    /// Clears the game installation cache.
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Replaces the cached copy of an install with one the store located again after the game updated. Every
    /// loadout resolves its installation through the cache, and the store's file list is looked up by the ids
    /// the copy carries, so a copy left on the old build makes the whole install read as modified.
    /// Does nothing when the cache holds no copy of that game, store and folder.
    /// </summary>
    void RefreshInstallation(GameInstallation located);

    /// <summary>
    /// Tries to get the game installation for a loadout.
    /// </summary>
    bool TryGetGameInstallation(Loadout.ReadOnly loadout, [NotNullWhen(true)] out GameInstallation? gameInstallation);

    /// <summary>
    /// Tries to get the game installation for a loadout and throws an exception if there isn't one.
    /// </summary>
    /// <exception cref="Exception">Thrown when there is no installation for the loadout</exception>
    GameInstallation ForceGetInstallation(Loadout.ReadOnly loadout)
    {
        if (!TryGetGameInstallation(loadout, out var installation)) throw new Exception($"Unable to find installation for Loadout {loadout.Name}");
        return installation;
    }

    /// <summary>
    /// Tries to get the installation metadata from the DB.
    /// </summary>
    bool TryGetMetadata(GameInstallation installation, out GameInstallMetadata.ReadOnly metadata);

    /// <summary>
    /// Tries to get the installation metadata from the DB and throws an exception if there isn't one.
    /// </summary>
    /// <exception cref="Exception">Thrown when there is no installation metadata in the DB</exception>
    GameInstallMetadata.ReadOnly ForceGetMetadata(GameInstallation installation)
    {
        if (!TryGetMetadata(installation, out var metadata)) throw new Exception($"Unable to find metadata for installation of {installation.Game.DisplayName} at {installation.LocatorResult.Path}");
        return metadata;
    }
}
