using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using R3;

namespace SilverModManager.Abstractions.Loadouts;

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
    /// <param name="loadout">The loadout to apply.</param>
    /// <param name="largeFiles">The answer to give when added files are over the backup cap; null asks the player.</param>
    /// <param name="cancellationToken">Ends the wait for that answer.</param>
    public Task Synchronize(LoadoutId loadout, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Removes the loadout's External Changes and applies it, which deletes those files from the game folder, backing
    /// them up first. Waits its turn like <see cref="Synchronize"/>, and the backup-cap question holds the install the
    /// same way. When the question is refused or the wait cancelled, the External Changes are back as they were.
    /// </summary>
    /// <param name="loadout">The loadout whose External Changes are removed.</param>
    /// <param name="largeFiles">The answer to give when the files to back up are over the cap; null asks the player.</param>
    /// <param name="cancellationToken">Ends the wait for that answer.</param>
    public Task CleanGameFolder(LoadoutId loadout, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all the loadouts for a game, and resets the game folder to its
    /// initial state.
    /// </summary>
    /// <param name="installation">Game installation which should be unmanaged.</param>
    /// <param name="runGc">If true, runs the garbage collector.</param>
    public Task UnManage(GameInstallation installation, bool runGc = true, bool cleanGameFolder = true, BackupOverflowChoice? largeFiles = null);
    
    /// <summary>
    /// Moves the root to a different deployment backend. The game is first reset with the backend that
    /// currently owns it, then the new choice is recorded and the last applied loadout, if any, is applied again.
    /// </summary>
    /// <param name="chosen">
    /// True when a person picked the backend, which marks the row chosen. False when the selection chain made the pick,
    /// which clears an earlier mark.
    /// </param>
    /// <returns>False when the root already uses <paramref name="backend"/> and nothing was done.</returns>
    /// <param name="largeFiles">The answer to give when the files to back up are over the cap; null asks the player.</param>
    /// <param name="cancellationToken">Ends the wait for that answer.</param>
    /// <throws cref="ExecutableInUseException">Thrown while the app is running a launch of this game.</throws>
    public Task<bool> ChangeDeploymentBackend(GameInstallation installation, GamePath root, DeploymentBackendId backend, bool chosen, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default);

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

