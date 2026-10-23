using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Abstractions;

/// <summary>
/// What every Bethesda title shares: a data folder, folders the synchronizer must leave alone, and the
/// intrinsic files the game wants written on deployment.
/// </summary>
public interface IBethesdaGame : IGame
{
    /// <summary>The folder plugins and loose mod files are installed into.</summary>
    GamePath DataPath { get; }

    /// <summary>Folders whose contents are never part of the loadout (saves and the like).</summary>
    IReadOnlyList<GamePath> ProtectedFolders { get; }

    /// <summary>
    /// Whether the synchronizer must leave a path alone. Defaults to being inside a protected folder; a game
    /// overrides it when it needs an exception inside one (a folder of which only one file is managed).
    /// </summary>
    bool IsProtectedPath(GamePath path);

    /// <summary>
    /// The files the app generates for this game. Takes the loadout because whether a game has an intrinsic
    /// file can depend on the locations of the installation, and the deployment backend resolves every offered
    /// path through those locations, so a file whose location is absent must not be offered.
    /// </summary>
    Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, IServiceProvider provider);
}
