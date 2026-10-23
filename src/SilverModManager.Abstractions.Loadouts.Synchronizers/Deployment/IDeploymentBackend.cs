using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Whether a backend accepts a root, and why.
/// </summary>
public readonly record struct DeploymentPreflight(bool Accepted, string Reason)
{
    public static DeploymentPreflight Accept(string reason) => new(true, reason);

    public static DeploymentPreflight Refuse(string reason) => new(false, reason);
}

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
    /// The staging store that files deployed to <paramref name="folder"/> link from, given the default
    /// store. Garbage collection keeps a staged file only in this store. A backend whose files link into
    /// the default store regardless of filesystem keeps this answer.
    /// </summary>
    ILooseStagingStore StagingStoreFor(ILooseStagingStore defaultStore, AbsolutePath folder) => defaultStore;

    /// <summary>
    /// Whether this backend can deploy <paramref name="root"/> on this machine right now. May create and
    /// delete probe files under the root and the staging store, so it must not run while the game does.
    /// </summary>
    /// <remarks>
    /// Environmental failures (wrong filesystem, missing permission) are a refusal with the reason, never an exception.
    /// </remarks>
    DeploymentPreflight Preflight(GameInstallation installation, GamePath root);

    /// <summary>
    /// Folders the game process has to be able to see for files deployed to <paramref name="root"/> to
    /// resolve, which matters inside the Steam runtime container: a symlink into the staging store dangles
    /// there unless the store is shared with it.
    /// </summary>
    IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root);

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
    /// Gives files that are already in the game folders the modification times a game wants, and records the
    /// times in the disk state. Files extracted in this apply are not passed: <see cref="Extract"/> sets
    /// <see cref="DeploymentContext.PlannedModifiedTimes"/> as it writes them.
    /// </summary>
    /// <remarks>
    /// A backend that cannot present a per-file time must override this and say so: the default throws, so a
    /// timestamp-ordered game is never silently deployed with the wrong load order.
    /// </remarks>
    Task StampTimestamps(DeploymentContext context, IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> stamps) =>
        throw new NotSupportedException($"The {Id} deployment backend cannot set file modification times, which this game needs for its load order.");

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
