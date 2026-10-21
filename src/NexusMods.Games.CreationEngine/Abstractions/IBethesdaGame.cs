using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Abstractions;

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
    /// The files the app generates for this game. Takes the loadout because whether a game has an intrinsic
    /// file can depend on the locations of the installation, and the deployment backend resolves every offered
    /// path through those locations, so a file whose location is absent must not be offered.
    /// </summary>
    Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout, IServiceProvider provider);
}
