using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

[PublicAPI]
public interface ILoadoutManager
{
    /// <summary>
    /// Manages the installation without creating a loadout.
    /// </summary>
    ValueTask<GameInstallMetadata.ReadOnly> ManageInstallation(GameInstallation installation);

    /// <summary>
    /// Creates a loadout for a game, managing the game if it has not previously been managed.
    /// </summary>
    IJobTask<CreateLoadoutJob, Loadout.ReadOnly> CreateLoadout(GameInstallation installation, string? suggestedName = null);

    /// <summary>
    /// Copies a loadout.
    /// </summary>
    ValueTask<Loadout.ReadOnly> CopyLoadout(LoadoutId loadoutId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a loadout. Names are unique per installation (ignoring case and surrounding whitespace), so a taken
    /// name gets a numeric suffix. Returns the loadout with its final name.
    /// </summary>
    ValueTask<Loadout.ReadOnly> RenameLoadout(LoadoutId loadoutId, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the loadout for the game. If the loadout is the currently active loadout,
    /// the game's folder will be reset to its initial state.
    /// </summary>
    ValueTask DeleteLoadout(LoadoutId loadoutId, GarbageCollectorRunMode gcRunMode = GarbageCollectorRunMode.DoNotRun, bool deactivateIfActive = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the loadout as the active loadout for the game, applying the changes to the game folder.
    /// </summary>
    ValueTask ActivateLoadout(LoadoutId loadoutId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets a game back to its initial state, any applied loadouts will be unapplied.
    /// </summary>
    ValueTask DeactivateCurrentLoadout(GameInstallation installation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the loadout's External Changes and synchronizes, which deletes those files from the game folder.
    /// Throws <see cref="SynchronizerPreflightException"/>, before any row is deleted, when the file hashes data does
    /// not cover the installed build and the game's own files could not be told from the added ones.
    /// </summary>
    Task CleanGameFolder(Loadout.ReadOnly loadout);

    /// <summary>
    /// Re-reads the game folder and drops the External Changes of <paramref name="loadout"/> whose files are no longer
    /// there, so the page matches the disk after something outside a sync changed the folder. Nothing is written to
    /// the game folder, and an External Change that was never on disk (still waiting to be deployed) is kept.
    /// </summary>
    Task RescanGameFolder(Loadout.ReadOnly loadout);

    /// <summary>
    /// Gets the currently active loadout for the game, if any.
    /// </summary>
    Optional<LoadoutId> GetCurrentlyActiveLoadout(GameInstallation installation);

    /// <summary>
    /// Removes all the loadouts for a game, and resets the game folder to its initial state.
    /// </summary>
    IJobTask<UnmanageGameJob, GameInstallation> UnManage(GameInstallation installation, bool runGc = true, bool cleanGameFolder = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Wrapper for item installations.
    /// </summary>
    Task<LoadoutItemGroup.ReadOnly> InstallItemWrapper(LoadoutId targetLoadout, Func<ITransaction, Task<LoadoutItemGroupId>> func);

    /// <summary>
    /// Installs a library item into a target loadout.
    /// </summary>
    /// <param name="libraryItem">The item to install.</param>
    /// <param name="targetLoadout">The target loadout.</param>
    /// <param name="parent">If specified the installed item will be placed in this group, otherwise it will default to the user's local collection</param>
    /// <param name="installer">The Library will use this installer to install the item</param>
    /// <param name="fallbackInstaller">The installer to use if the default installer fails</param>
    /// <remarks>
    /// Job returns a result with null <see cref="LoadoutItemGroup.ReadOnly"/> after
    /// if supplied an external transaction via <paramref name="transaction"/>,
    /// since it is the caller's responsibility to complete that transaction.
    /// </remarks>
    IJobTask<IInstallLoadoutItemJob, InstallLoadoutItemJobResult> InstallItem(
        LibraryItem.ReadOnly libraryItem,
        LoadoutId targetLoadout,
        Optional<LoadoutItemGroupId> parent = default,
        ILibraryItemInstaller? installer = null,
        ILibraryItemInstaller? fallbackInstaller = null);

    /// <summary>
    /// Removes the items from their Loadouts.
    /// </summary>
    ValueTask RemoveItems(LoadoutItemGroupId[] groupIds);

    /// <summary>
    /// Removes a collection.
    /// </summary>
    ValueTask RemoveCollection(CollectionGroupId collection);

    /// <summary>
    /// Clones a collection.
    /// </summary>
    ValueTask<CollectionGroup.ReadOnly> CloneCollection(CollectionGroupId collection);

    /// <summary>
    /// Applies the collection download rules for an installed collection.
    /// </summary>
    ValueTask ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId collectionId);

    /// <summary>
    /// Removes all groups and installs the new library item.
    /// </summary>
    ValueTask ReplaceItems(LoadoutId loadoutId, LoadoutItemGroupId[] groupsToRemove, LibraryItem.ReadOnly libraryItemToInstall);

    /// <summary>
    /// Resolves file conflicts by making items in <paramref name="winnerIds"/> win over <paramref name="loserId"/>.
    /// </summary>
    ValueTask ResolveFileConflicts(LoadoutItemGroupPriorityId[] winnerIds, LoadoutItemGroupPriorityId loserId);

    /// <summary>
    /// Make items in <paramref name="losers"/> lose all file conflicts.
    /// </summary>
    ValueTask LoseAllFileConflicts(LoadoutItemGroupPriorityId[] loserIds);

    /// <summary>
    /// Make items in <paramref name="winnerIds"/> win all file conflicts.
    /// </summary>
    ValueTask WinAllFileConflicts(LoadoutItemGroupPriorityId[] winnerIds);

    /// <summary>
    /// Adds an ordering rule between two top-level groups of a loadout and moves the priorities to satisfy it,
    /// together with every rule already stored. When the rule contradicts the stored ones the result names the
    /// conflicting groups and nothing is written.
    /// </summary>
    ValueTask<AddGroupRuleResult> AddGroupRule(LoadoutId loadoutId, LoadoutItemGroupId source, LoadoutItemGroupId other, GroupRuleKind kind);

    /// <summary>
    /// Deletes a group rule. Priorities stay where they are: removing a constraint never moves a group.
    /// </summary>
    ValueTask RemoveGroupRule(LoadoutGroupRuleId ruleId);
}
