using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Carries out the file-touching phases of a synchronize for the part of the game the
/// <see cref="DeploymentPlan"/> routes to it.
/// </summary>
/// <remarks>
/// The nodes passed to each phase are the sync-tree entries whose path the plan routes to this backend
/// (the whole tree when one backend owns everything). Each phase filters on <c>node.Actions</c> itself.
/// </remarks>
public interface IDeploymentBackend
{
    DeploymentBackendId Id { get; }

    /// <summary>
    /// Whether this backend reads file contents from the loose staging store. Only roots routed to such
    /// backends keep staged files alive in garbage collection.
    /// </summary>
    bool UsesLooseStaging { get; }

    /// <summary>
    /// Backs up original game files that are about to be replaced. Commits its own transaction, before the apply's.
    /// </summary>
    Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes);

    /// <summary>
    /// Adds files found on disk to the loadout. <see cref="DeploymentContext.Loadout"/> is required.
    /// </summary>
    void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes);

    /// <summary>
    /// Updates the loadout to match changes made to intrinsic files on disk.
    /// <see cref="DeploymentContext.Loadout"/> is required.
    /// </summary>
    Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes);

    /// <summary>
    /// Removes files from the game folders.
    /// </summary>
    /// <returns>The parents of files that were deleted and not extracted again.</returns>
    IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes);

    /// <summary>
    /// Writes loadout files into the game folders.
    /// </summary>
    Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes);

    /// <summary>
    /// Writes generated files such as plugin lists. <see cref="DeploymentContext.Loadout"/> is required.
    /// </summary>
    Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes);

    /// <summary>
    /// Records deletes of game files so they stay deleted in later synchronizes.
    /// <see cref="DeploymentContext.Loadout"/> is required.
    /// </summary>
    void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes);

    /// <summary>
    /// Removes folders emptied by <see cref="Delete"/>. Runs after the transaction has been committed, so
    /// <see cref="DeploymentContext.Transaction"/> must not be used.
    /// </summary>
    void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState);
}
