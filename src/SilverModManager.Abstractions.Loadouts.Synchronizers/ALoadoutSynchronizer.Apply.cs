using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Abstractions.Loadouts.Sorting;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

using DiskState = Entities<DiskStateEntry.ReadOnly>;

public partial class ALoadoutSynchronizer
{
    /// <inheritdoc />
    public async Task<Loadout.ReadOnly> RunActions(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null, BackupOverflowChoice choice = BackupOverflowChoice.BackUpAnyway)
    {
        await Preflight(loadout.InstallationInstance);
        var gameMetadataId = MetadataOf(loadout).GameInstallMetadataId;
        RespellTree(syncTree, _applySpellings);
        await PreflightTree(syncTree, loadout.InstallationInstance, loadout, choice);

        using var _ = await _lock.LockAsync();
        using var tx = Connection.BeginTransaction();
        using var pins = TransactionPins.Hold(tx);

        // Before the backends copy the tree: a vanilla file the store vouched for is read here, once, so what is
        // archived and recorded is the bytes' xxHash3 and the attestation is settled before anything is written.
        await ResolveDiskIdentitiesForWrites(syncTree, loadout.InstallationInstance, tx, choice, job: job);

        // Held from before the chain probes the store directory until the links are recorded: a sweep in
        // between cannot see the files this apply is staging and would delete them.
        using var stagingHold = _stagingStore.Hold();
        var plan = _planStore.EnsureComplete(tx, Connection.Db, loadout.InstallationInstance, gameMetadataId, _backendSelector);
        var groups = GroupByBackend(plan, syncTree);
        PreflightRecordedRoots(loadout.InstallationInstance, plan, syncTree);
        IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> plannedTimes = loadout.InstallationInstance.Game is ITimestampOrderedGame timestamped
            ? await timestamped.PluginTimestamps(loadout, syncTree)
            : [];
        var context = new DeploymentContext
        {
            Synchronizer = this,
            Installation = loadout.InstallationInstance,
            MetadataId = gameMetadataId,
            SyncTree = syncTree,
            Loadout = loadout,
            Transaction = tx,
            Job = job,
            PlannedModifiedTimes = plannedTimes.ToDictionary(t => t.Path, t => t.Mtime),
        };

        var foldersWithDeletedFiles = await RunActionsInOrder(groups, context);

        // After the loop so every file is in place: the ones extracted got their time as they were written, the
        // ones already there get theirs now.
        if (plannedTimes.Count > 0)
        {
            foreach (var group in groups)
                await group.Backend.StampTimestamps(context, plannedTimes.Where(t => group.Nodes.ContainsKey(t.Path)).ToList());
        }

        job?.SetStatus(SyncPhases.RecordingChanges);
        if (job is not null) job.UnwritableFiles = UnwritableFiles.Of(syncTree);
        
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastSyncedLoadout, loadout.Id);
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
        // This sync took in whatever the pending launch finish was owed, so the marker goes in the same commit: no
        // window where the sync is recorded and the marker still asks for another one.
        Sdk.Games.GameInstallMetadata.RetractLaunchFinishMarker(tx, gameMetadataId);
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastApplyLeftFilesUnextracted,
            syncTree.Values.Any(node => node.Actions.HasFlag(Actions.WarnOfUnableToExtract)));
        tx.Add(loadout.Id, Loadout.LastAppliedDateTime, DateTime.UtcNow);
        // Read before the commit: an archive that lands in between then counts as a change after this Apply, and the
        // status compares once more than it needs to rather than missing it.
        var archivesVersion = _fileStore.ArchivesVersion;
        var applied = await tx.Commit();
        _archiveVersionsAtApply.Record(gameMetadataId, applied.NewTx, archivesVersion);

        loadout = loadout.Rebase();
        var newState = DiskStateEntry.FindByGame(loadout.Db, loadout.Installation);

        CleanEmptyDirectories(groups, foldersWithDeletedFiles, context, newState);
        LogSummary($"Sync of loadout {loadout.Name} ({loadout.LoadoutId})", syncTree, groups, context, plan);

        // The collector sweeps the staging store, which waits for every hold.
        stagingHold.Dispose();

        job?.SetStatus(SyncPhases.CleaningUp);
        await _garbageCollectorRunner.RunAsync();

        // Last, once the Apply is recorded: a failed Apply never reaches here and leaves the caches alone.
        _cacheClearer.Clear(loadout.InstallationInstance, ChangedGamePaths(syncTree));

        return loadout;
    }

    /// <summary>
    /// Alternative to <see cref="RunActions"/> that ignores changes and optionally clears the last sync loadout metadata
    /// </summary>
    public async Task RunActions(Dictionary<GamePath, SyncNode> syncTree, GameInstallation gameInstallation, BackupOverflowChoice choice = BackupOverflowChoice.BackUpAnyway)
    {
        await Preflight(gameInstallation);

        // The initial state can name folders the disk no longer spells that way (a merge ran since), and writing there
        // would bring the twin back. The spelling comes from the disk as it is now: a reset does not merge, so a folder
        // still spelled two ways stays as the disk has it and paths are restored into the folder they came from.
        if (MergesCaseVariantFoldersOnApply(gameInstallation))
            RespellTree(syncTree, BuildSpellings(gameInstallation, null, _gameRegistry.ForceGetMetadata(gameInstallation), keepSplit: true));
        await PreflightTree(syncTree, gameInstallation, null, choice);

        using var _ = await _lock.LockAsync();
        using var tx = Connection.BeginTransaction();

        await ResolveDiskIdentitiesForWrites(syncTree, gameInstallation, tx, choice);

        var metadata = _gameRegistry.ForceGetMetadata(gameInstallation);

        // See the loadout overload. Recorded roots are not preflighted here: a root that no longer accepts
        // its backend still has to be reset through that backend before it can be moved to another one.
        using var stagingHold = _stagingStore.Hold();
        var plan = _planStore.EnsureComplete(tx, Connection.Db, gameInstallation, metadata, _backendSelector);
        var groups = GroupByBackend(plan, syncTree);
        var context = new DeploymentContext
        {
            Synchronizer = this,
            Installation = gameInstallation,
            MetadataId = metadata,
            SyncTree = syncTree,
            Transaction = tx,
        };

        var foldersWithDeletedFiles = await RunActionsInOrder(groups, context);

        if (metadata.Contains(Sdk.Games.GameInstallMetadata.LastSyncedLoadout))
        {
            tx.Retract(metadata, Sdk.Games.GameInstallMetadata.LastSyncedLoadout, (EntityId)metadata.LastSyncedLoadout);
            tx.Retract(metadata, Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction, (EntityId)metadata.LastSyncedLoadoutTransaction);
        }

        // With no last apply there is nothing for the flag to describe.
        tx.RetractCurrent(metadata.Id, Sdk.Games.GameInstallMetadata.LastApplyLeftFilesUnextracted);

        // With no last sync the marker's stale checks can't tell it from a live one; after a re-manage it would
        // report a loadout deleted that never existed in this install.
        Sdk.Games.GameInstallMetadata.RetractLaunchFinishMarker(tx, metadata.Id);

        tx.Add(metadata, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));

        var result = await tx.Commit();

        var newMetadata = metadata.Rebase(result.Db);

        CleanEmptyDirectories(groups, foldersWithDeletedFiles, context, DiskStateEntry.FindByGame(newMetadata.Db, newMetadata));
        LogSummary($"Reset of the game folder of {gameInstallation.Game.DisplayName}", syncTree, groups, context, plan);
    }

    /// <summary>
    /// Runs <see cref="ActionsInOrder"/> through every backend group, reporting each phase to the context's job (none
    /// for a reset). With no loadout in the context (a reset), the actions that only make sense inside one are
    /// skipped. Returns, per group, the folders that lost a file in the delete phase.
    /// </summary>
    private async Task<List<IReadOnlyCollection<GamePath>>> RunActionsInOrder(List<BackendGroup> groups, DeploymentContext context)
    {
        var job = context.Job;
        var syncTree = context.SyncTree;
        var foldersWithDeletedFiles = new List<IReadOnlyCollection<GamePath>>();

        foreach (var action in ActionsInOrder)
        {
            switch (action)
            {
                case Actions.DoNothing:
                    break;

                case Actions.BackupFile:
                    job?.SetStatus(SyncPhases.BackingUp);
                    foreach (var group in groups) await group.Backend.Backup(context, group.Nodes);
                    await RootStoredFilesBeingReplaced(context);
                    break;

                case Actions.IngestFromDisk:
                    if (SkipOutsideLoadout(context, action, "Cannot ingest files from disk when not in a loadout context")) break;
                    job?.SetStatus(SyncPhases.IngestingChanges);
                    foreach (var group in groups) group.Backend.Ingest(context, group.Nodes);
                    break;

                case Actions.AdaptLoadout:
                    if (SkipOutsideLoadout(context, action, "Cannot adapt loadout when not in a loadout context")) break;
                    job?.SetStatus(SyncPhases.UpdatingLoadout);
                    foreach (var group in groups) await group.Backend.AdaptLoadout(context, group.Nodes);
                    break;

                case Actions.DeleteFromDisk:
                    job?.SetStatus(SyncPhases.Deleting);
                    foreach (var group in groups) foldersWithDeletedFiles.Add(group.Backend.Delete(context, group.Nodes));
                    break;

                case Actions.ExtractToDisk:
                    job?.SetStatus(SyncPhases.Deploying);
                    foreach (var group in groups) await group.Backend.Extract(context, group.Nodes);
                    break;

                case Actions.WriteIntrinsic:
                    if (SkipOutsideLoadout(context, action, "Cannot write intrinsic files when not in a loadout context")) break;
                    job?.SetStatus(SyncPhases.WritingIntrinsics);
                    foreach (var group in groups) await group.Backend.WriteIntrinsics(context, group.Nodes);
                    break;

                case Actions.AddReifiedDelete:
                    if (SkipOutsideLoadout(context, action, "Cannot add reified deletes when not in a loadout context")) break;
                    job?.SetStatus(SyncPhases.UpdatingDeletedFiles);
                    foreach (var group in groups)
                    {
                        foreach (var (path, _) in group.Nodes.Where(n => n.Value.PlayerDeleted))
                            Logger.LogInformation("Recording the player's deletion of {Path}: it is gone from the game folder and the row providing it was the player's, not a mod's", path);
                        group.Backend.AddReifiedDeletes(context, group.Nodes);
                    }
                    break;

                case Actions.WarnOfUnableToExtract:
                    WarnOfUnableToExtract(syncTree);
                    break;

                case Actions.WarnOfConflict:
                    WarnOfConflict(syncTree);
                    break;

                case Actions.ForgetDiskState:
                    ActionForgetDiskState(syncTree, context.Transaction);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown action: {action}");
            }
        }

        return foldersWithDeletedFiles;
    }

    /// <summary>
    /// True when the context has no loadout, so the action is skipped. A debug build refuses a tree that carries the
    /// action instead, because it means the caller built the tree for the wrong overload.
    /// </summary>
    private static bool SkipOutsideLoadout(DeploymentContext context, Actions action, string message)
    {
        if (context.Loadout is not null) return false;
        if (ApplicationConstants.IsDebug && context.SyncTree.Any(n => n.Value.Actions.HasFlag(action)))
            throw new InvalidOperationException(message);
        return true;
    }

    /// <summary>Removes each group's folders that lost a file in the delete phase, judged against the committed state.</summary>
    private static void CleanEmptyDirectories(List<BackendGroup> groups, List<IReadOnlyCollection<GamePath>> foldersWithDeletedFiles, DeploymentContext context, DiskState newState)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            if (i < foldersWithDeletedFiles.Count && foldersWithDeletedFiles[i].Count > 0)
                groups[i].Backend.CleanDirectories(context, foldersWithDeletedFiles[i], newState);
        }
    }

    private void LogSummary(string subject, Dictionary<GamePath, SyncNode> tree, List<BackendGroup> groups, DeploymentContext context, DeploymentPlan plan)
        => Logger.LogInformation("{Summary}", SyncSummary.Describe(subject, tree, groups.Select(static g => (g.Backend.Id, g.Nodes)), context.EmptyFoldersRemoved,
            plan.Roots.Where(static r => r.Backend == DeploymentBackendId.Fuse).Select(static r => r.Root).ToArray()));

    /// <summary>
    /// The part of the sync tree one backend carries out.
    /// </summary>
    private sealed record BackendGroup(IDeploymentBackend Backend, IReadOnlyDictionary<GamePath, SyncNode> Nodes);

    /// <summary>
    /// Splits the tree by the backend the plan routes each path to, copy first and then by id, so phases run
    /// in the same order every time. When one backend owns everything it gets the original dictionary.
    /// </summary>
    /// <remarks>
    /// Every backend the plan names must be registered, even one with no nodes in this tree: a plan row pointing at
    /// a missing backend means the root's files are managed by something this run cannot undo. The backup size cap
    /// applies per group once a plan splits the tree.
    /// </remarks>
    private List<BackendGroup> GroupByBackend(DeploymentPlan plan, Dictionary<GamePath, SyncNode> syncTree)
    {
        foreach (var root in plan.Roots)
        {
            if (_backends.ContainsKey(root.Backend)) continue;
            throw new InvalidOperationException(
                $"Deployment backend '{root.Backend}' of root '{root.Root}' is not registered. " +
                "Switch the root back with ISynchronizerService.ChangeDeploymentBackend."
            );
        }

        var routed = new Dictionary<DeploymentBackendId, Dictionary<GamePath, SyncNode>>();
        foreach (var (path, node) in syncTree)
        {
            var id = plan.BackendFor(path);
            if (!routed.TryGetValue(id, out var nodes)) routed[id] = nodes = new Dictionary<GamePath, SyncNode>();
            nodes.Add(path, node);
        }

        if (routed.Count <= 1)
        {
            var only = routed.Count == 1 ? routed.Keys.Single() : DeploymentBackendId.Copy;
            return [new BackendGroup(_backends[only], syncTree)];
        }

        return routed
            .OrderBy(kv => kv.Key == DeploymentBackendId.Copy ? 0 : 1)
            .ThenBy(kv => kv.Key.Value, StringComparer.Ordinal)
            .Select(kv => new BackendGroup(_backends[kv.Key], kv.Value))
            .ToList();
    }

    private void WarnOfConflict(Dictionary<GamePath, SyncNode> tree)
    {
        
        foreach (var (path, node) in tree)
        {
            if (!node.Actions.HasFlag(Actions.WarnOfConflict))
                continue;
            Logger.LogWarning("Conflict in {Path}", path);
        }
    }

    /// <summary>
    /// Refuses, before anything is written, an apply that has to write a file whose bytes the store does not hold.
    /// The extraction would fail halfway with the store's own error; this names the files instead.
    /// </summary>
    private async ValueTask RefuseExtractionsWithoutBytes(Dictionary<GamePath, SyncNode> syncTree, bool forReset)
    {
        List<GamePath>? missing = null;
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.ExtractToDisk) || node.Loadout.Identity.IsNone) continue;
            if (await HaveArchiveAsync(node.Loadout.Identity, node.Loadout.Size)) continue;
            (missing ??= []).Add(path);
        }

        if (missing is null) return;
        Logger.LogInformation("{Count} file(s) to write are not in the file store: {Paths}", missing.Count, string.Join(", ", missing.Take(20)));
        throw forReset ? MissingFileBytesException.ForReset(missing) : new MissingFileBytesException(missing);
    }

    private void WarnOfUnableToExtract(Dictionary<GamePath, SyncNode> groupings)
    {
        foreach (var (path, node) in groupings)
        {
            if (!node.Actions.HasFlag(Actions.WarnOfUnableToExtract))
                continue;
            Logger.LogWarning("Unable to extract {Path}: no copy of it is kept and it is {Why}", path, UnwritableFiles.ReasonOf(node) == UnwritableReason.NotOnDisk ? "not on disk" : "on disk with different contents");
        }
    }

    private void ActionForgetDiskState(Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.ForgetDiskState)) continue;

            if (node.DiskIsGameOwn)
                Logger.LogInformation("Leaving `{Path}` on disk: the store put the game's own bytes back over an earlier apply's file and no loadout row names it; no longer tracked", path);
            else if (IsGameOutputOnFilteredPath(path))
                Logger.LogInformation("Leaving `{Path}` on disk: the game or a mod's cache writes it, so it is not the loadout's; no longer tracked", path);
            else
                Logger.LogWarning("Leaving `{Path}` on disk: it differs from what the loadout deployed, so it is the player's and no longer tracked", path);
            DiskStateEntries.Retract(tx, node);
        }
    }

    /// <summary>
    /// A game file the apply deletes or overwrites while the store already holds its bytes gets no <see cref="Actions.BackupFile"/>,
    /// so the backup's record of it is never made, and what keeps those bytes in the store is whatever happened to refer to
    /// them (a job's pin, a loadout row that is about to go). Once nothing does, the garbage collector takes them and
    /// the file cannot be put back. This records the same <see cref="GameBackedUpFile"/> a backup would for every
    /// such file, in a transaction of its own that commits before the delete step runs, as the backup's does.
    /// Only the game's own bytes are rooted: a mod file that an apply removes is in the store because the mod put it
    /// there, and it must stay collectable once the mod is gone.
    /// </summary>
    private async Task RootStoredFilesBeingReplaced(DeploymentContext context)
    {
        List<FileIdentity>? toRoot = null;
        Dictionary<GamePath, SyncNodePart>? initial = null;
        Dictionary<string, GameFileRecord>? listed = null;
        foreach (var (path, node) in context.SyncTree)
        {
            if (!node.HaveDisk || !node.Actions.HasFlag(Actions.DeleteFromDisk) || node.Actions.HasFlag(Actions.BackupFile)) continue;
            if (!node.Signature.HasFlag(Signature.DiskArchived)) continue;

            initial ??= InitialDiskState(context.MetadataId);
            if (!IsGameOwnBytes(_identityResolver, context.Installation, path, node.Disk, !node.Signature.HasFlag(Signature.DiskEqualsPrev), initial, ref listed)) continue;

            var key = node.Disk.Identity.Kind == HashKind.XxHash3
                ? node.Disk.Identity
                : await _identityResolver.StoreKeyAsync(_fileStore, node.Disk.Identity, node.Disk.Size);
            if (key is not { } storeKey) continue;

            Logger.LogInformation("Rooting the stored copy {Key} of {Path}: the apply replaces it without a backup because the store already holds the bytes", storeKey, path);
            (toRoot ??= []).Add(storeKey);
        }

        if (toRoot is not null) await GameBackedUpFile.Root(Connection, context.MetadataId, toRoot);
    }

    private Dictionary<GamePath, SyncNodePart> InitialDiskState(GameInstallMetadataId metadataId)
    {
        var metadata = GameInstallMetadata.Load(Connection.Db, metadataId);
        return GameInstallMetadata.InitialDiskStateTransaction.TryGetValue(metadata, out var initialTx)
            ? ((ILoadoutSynchronizer)this).GetDiskStateForGameAsOf(metadata, TxId.From(initialTx.Value)).ToDictionary(p => p.Path, p => p.Part)
            : [];
    }

    /// <summary>
    /// Whether the bytes on disk at <paramref name="path"/> are the game's own: what the install was first indexed
    /// with, or what the store's file list names for that path. A file whose bytes a mod deployed is neither, so it
    /// is not the game's to keep. A list that names the file by path and size but cannot be joined to the disk's
    /// digest counts when the disk is not what an earlier apply wrote.
    /// </summary>
    private bool IsGameOwnBytes(IFileIdentityResolver resolver, GameInstallation installation, GamePath path, SyncNodePart disk, bool diskDiffersFromPrevious, Dictionary<GamePath, SyncNodePart> initial, ref Dictionary<string, GameFileRecord>? listed)
    {
        if (initial.TryGetValue(path, out var original)
            && original.Size == disk.Size
            && resolver.Resolve(original.Identity, disk.Identity, disk.Size) == IdentityMatch.Same)
            return true;

        if (path.LocationId != LocationId.Game) return false;
        listed ??= ListedGameFiles(installation);
        if (!listed.TryGetValue(path.Path.ToString(), out var record) || record.Size != disk.Size) return false;

        return resolver.Resolve(record.Identity, disk.Identity, disk.Size) switch
        {
            IdentityMatch.Same => true,
            IdentityMatch.Unknown => diskDiffersFromPrevious,
            _ => false,
        };
    }

    /// <summary>
    /// All actions, in execution order.
    /// </summary>
    private static readonly Actions[] ActionsInOrder = Enum.GetValues<Actions>().OrderBy(a => (ushort)a).ToArray();

    /// <summary>
    /// Returns true if the bytes behind this identity have been archived. The file store is keyed by xxHash3, so
    /// an xxHash3 identity is asked as it is, a SHA-1 identity is joined to the xxHash3 of the same bytes through a
    /// relation the app computed from them, and any other kind is not archived. A relation is the one place two
    /// kinds may be joined, because it holds digests of the same bytes.
    /// </summary>
    /// <param name="size">A relation of another size is not the same file.</param>
    protected bool HaveArchive(FileIdentity identity, Size size) => HaveArchive(_identityResolver, identity, size);

    /// <summary>
    /// <see cref="HaveArchive(FileIdentity, Size)"/> through <paramref name="resolver"/>, so a pass that holds a scoped
    /// resolver puts its questions to the memo instead of the recorded digests.
    /// </summary>
    private bool HaveArchive(IFileIdentityResolver resolver, FileIdentity identity, Size size)
    {
        return HaveArchiveAsync(resolver, identity, size).AsTask().Result;
    }

    private ValueTask<bool> HaveArchiveAsync(FileIdentity identity, Size size) => HaveArchiveAsync(_identityResolver, identity, size);

    private async ValueTask<bool> HaveArchiveAsync(IFileIdentityResolver resolver, FileIdentity identity, Size size)
    {
        if (identity.Kind == HashKind.XxHash3) return await _fileStore.HaveFile(identity);
        return identity.Kind == HashKind.Sha1 && await resolver.StoreKeyAsync(_fileStore, identity, size) is not null;
    }

    private static FileIdentity ParseIdentity(string? hex) => string.IsNullOrEmpty(hex) ? FileIdentity.None : FileIdentity.ParseHex(hex);
}
