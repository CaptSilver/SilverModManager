using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Files.Diff;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using R3;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// Singleton service for applying loadouts and tracking what loadouts are currently applied/being applied.
/// </summary>
public interface ISynchronizerService
{
    /// <summary>
    /// Synchronize the loadout with the game folder, any changes in the game folder will be added to the loadout, and any
    /// new changes in the loadout will be applied to the game folder.
    /// </summary>
    /// <throws cref="ExecutableInUseException">Thrown while the app is running a launch of this game.</throws>
    public Task Synchronize(LoadoutId loadout);
    
    /// <summary>
    /// Removes all the loadouts for a game, and resets the game folder to its
    /// initial state.
    /// </summary>
    /// <param name="installation">Game installation which should be unmanaged.</param>
    /// <param name="runGc">If true, runs the garbage collector.</param>
    public Task UnManage(GameInstallation installation, bool runGc = true, bool cleanGameFolder = true);
    
    /// <summary>
    /// Moves the root to a different deployment backend. The game is first reset with the backend that
    /// currently owns it, then the new choice is recorded and the last applied loadout, if any, is applied again.
    /// </summary>
    /// <param name="chosen">
    /// True when a person picked the backend, which marks the row chosen. False when the selection chain made the pick,
    /// which clears an earlier mark.
    /// </param>
    /// <returns>False when the root already uses <paramref name="backend"/> and nothing was done.</returns>
    /// <throws cref="ExecutableInUseException">Thrown while the app is running a launch of this game.</throws>
    public Task<bool> ChangeDeploymentBackend(GameInstallation installation, GamePath root, DeploymentBackendId backend, bool chosen);

    /// <summary>
    /// Get the diff tree of the unapplied changes of a loadout.
    /// </summary>
    public FileDiffTree GetApplyDiffTree(LoadoutId loadout);
    
    /// <summary>
    /// Returns the last applied loadout for a given game installation.
    /// </summary>
    public bool TryGetLastAppliedLoadout(GameInstallation gameInstallation, out Loadout.ReadOnly loadout);
    
    /// <summary>
    /// Returns an observable of the last applied revisions for a specific game installation
    /// </summary>
    public IObservable<LoadoutWithTxId> LastAppliedRevisionFor(GameInstallation gameInstallation);

    /// <summary>
    /// Returns an observable of the status of the synchronizer for a specific game installation.
    /// </summary>
    public IObservable<GameSynchronizerState> StatusForGame(GameInstallMetadataId gameInstallId);
    
    /// <summary>
    /// Returns an observable of the status of the synchronizer for a specific loadout.
    /// </summary>
    public Task<IObservable<LoadoutSynchronizerState>> StatusForLoadout(LoadoutId loadoutId);
}

