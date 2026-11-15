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
    public async Task ResetToOriginalGameState(GameInstallation installation, LocatorId[] locatorIds, BackupOverflowChoice? largeFiles = null, CancellationToken cancellationToken = default, bool discardBackups = false)
    {
        SyncOrigin.LogStart(Logger, $"reset of the game folder of {installation.Game.DisplayName}");

        // Same reasoning as in Synchronize: the re-index below writes before RunActions would refuse.
        await Preflight(installation);
        EnsureBuildCovered(installation, locatorIds);

        var desiredState = await BuildResetTree(installation, locatorIds);

        // The caller is about to let go of every copy this reset would make (an unmanage), so none is worth the
        // time or the question.
        if (discardBackups)
        {
            DropBackups(desiredState);
            await RunActions(desiredState, installation, BackupOverflowChoice.BackUpAnyway);
            return;
        }

        // Every overflow node here is backed up and then deleted, so nothing is adoptable and the answer is back up
        // or refuse; an adopt given for the steps before it can only mean back up here. After a question the tree
        // is built again from a fresh index of the folder.
        var (choice, asked) = await ResolveBackupOverflow(desiredState, installation, largeFiles, cancellationToken, adoptOnlyBacksUp: true, operation: BackupOverflowOperation.Reset);
        if (asked)
        {
            desiredState = await BuildResetTree(installation, locatorIds);
            (choice, _) = await ResolveBackupOverflow(desiredState, installation, choice, cancellationToken, presetIsAnAnswer: true, adoptOnlyBacksUp: true, operation: BackupOverflowOperation.Reset);
        }

        // Run the groupings
        await RunActions(desiredState, installation, choice);
    }

    private static void DropBackups(Dictionary<GamePath, SyncNode> tree)
    {
        foreach (var path in tree.Keys.ToArray())
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            node.Actions &= ~Actions.BackupFile;
        }
    }

    /// <summary>
    /// Indexes the folder and builds the processed tree that takes it back to the vanilla file list.
    /// </summary>
    private async Task<Dictionary<GamePath, SyncNode>> BuildResetTree(GameInstallation installation, LocatorId[] locatorIds)
    {
        var gameState = _fileHashService.GetGameFiles(installation, locatorIds);
        var metadata = await ReindexState(installation);

        var diskStateEntries = DiskStateEntry.FindByGame(metadata.Db, metadata);

        List<PathPartPair> diskState = [];

        foreach (var diskFile in diskStateEntries)
        {
            diskState.Add(new PathPartPair(diskFile.Path, new SyncNodePart
            {   
                EntityId = diskFile.Id,
                Identity = diskFile.IdentityOf(),
                Size = diskFile.Size,
                LastModifiedTicks = diskFile.LastModified.UtcTicks,
            }));
        }

        Dictionary<GamePath, SyncNode> desiredState = new();

        foreach (var gameFile in gameState)
        {
            var part = new SyncNodePart
            {
                Identity = gameFile.Identity,
                Size = gameFile.Size,
                LastModifiedTicks = 0,
            };
            var syncNode = new SyncNode
            {
                Loadout = part,
                SourceItemType = LoadoutSourceItemType.Game,
            };
            desiredState.Add(gameFile.Path, syncNode);
        }

        var forgotten = AddDesiredStateOutsideGame(desiredState, metadata, diskState);
        await KeepGameInstalledContentThroughReset(installation, metadata, diskState, desiredState);

        // Has to run before MergeStates: whether the rendered file's bytes are in the file store is read while
        // the tree is processed, and a node without an archive would be mapped to a delete.
        await RenderIntrinsicFilesForReset(metadata, desiredState);

        // Merge the states into a tree. Passing in the current state as the current and previous state. 
        // This fakes the synchronizer into thinking that there are no changes on disk and we only want to do a
        // hard reset to the desired state.
        var onDisk = OverlayMountManifests(MountManifests(metadata.Id), diskState).ToList();
        MergeStates(onDisk, onDisk, desiredState);
        
        // Process the tree
        ProcessSyncTree(desiredState);

        // Tracked saves the game rewrote stay on disk, but nothing may track them any more.
        foreach (var path in forgotten) CollectionsMarshal.GetValueRefOrNullRef(desiredState, path).Actions = Actions.ForgetDiskState;

        return desiredState;
    }

    /// <summary>
    /// Fills in the desired state for every indexed file outside the <see cref="LocationId.Game"/> location.
    /// </summary>
    /// <remarks>
    /// Game files have a known vanilla state, so a reset can hard-set them. Anything else (configs,
    /// preferences) is the user's: without an entry here the reset would see "no desired file" and delete it.
    /// Only what the last synced loadout deployed is undone. A deployed file goes back to its copy from before
    /// the game was managed, or is removed if there was none. Files from the loadout's overrides, and files the
    /// loadout never touched, are kept as they are.
    /// </remarks>
    /// <returns>
    /// The tracked files on protected paths that the reset leaves alone because they no longer match what the
    /// loadout deployed (the game rewrote them). They are the player's, so they stay on disk and lose their
    /// disk state entry.
    /// </returns>
    private List<GamePath> AddDesiredStateOutsideGame(Dictionary<GamePath, SyncNode> desiredState, GameInstallMetadata.ReadOnly metadata, List<PathPartPair> diskState)
    {
        List<GamePath> forgotten = [];
        var deployed = GetFilesDeployedByLastSyncedLoadout(metadata, diskState);
        var initialState = GameInstallMetadata.InitialDiskStateTransaction.TryGetValue(metadata, out var initialTx)
            ? ((ILoadoutSynchronizer)this).GetDiskStateForGameAsOf(metadata, TxId.From(initialTx.Value))
                .ToDictionary(p => p.Path, p => p.Part)
            : new Dictionary<GamePath, SyncNodePart>();

        foreach (var (path, diskPart) in diskState)
        {
            if (path.LocationId == LocationId.Game) continue;

            if (!deployed.Contains(path))
            {
                desiredState.Add(path, new SyncNode { Loadout = diskPart, SourceItemType = LoadoutSourceItemType.Game });

                // Only a deploy puts a protected path in the disk state. One that no longer counts as deployed
                // (usually because the game rewrote it) is kept, so stop tracking it rather than leave it indexed.
                if (GamePathFilter.ShouldFilter(path)) forgotten.Add(path);
                continue;
            }

            if (!initialState.TryGetValue(path, out var initialPart))
            {
                // The loadout created this file: nothing to restore, so it has no desired state and is deleted.
                continue;
            }

            if (!HaveArchive(initialPart.Identity, initialPart.Size))
            {
                Logger.LogWarning("Initial copy of `{Path}` is missing from the file store, deleting it instead of restoring", path);
                continue;
            }

            desiredState.Add(path, new SyncNode
            {
                Loadout = new SyncNodePart { Identity = initialPart.Identity, Size = initialPart.Size, LastModifiedTicks = 0 },
                SourceItemType = LoadoutSourceItemType.Game,
            });
        }

        return forgotten;
    }

    /// <summary>
    /// The indexed files, in any location, that the last synced loadout put on disk from a mod or an intrinsic
    /// file. Files that came from the loadout's overrides were ingested from the user's own files, so they are
    /// not included. A loadout that no longer exists yields an empty set.
    /// </summary>
    internal HashSet<GamePath> GetFilesDeployedByLastSyncedLoadout(GameInstallMetadata.ReadOnly metadata, List<PathPartPair> diskState)
    {
        var result = new HashSet<GamePath>();
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSynced)) return result;

        var loadout = Loadout.Load(metadata.Db, lastSynced);
        if (!loadout.IsValid()) return result;

        var overridesGroup = OverridesGroupLookup.Find(loadout);

        // The tree is only read for what the loadout provides, so the disk state doubles as the previous state.
        foreach (var (path, node) in BuildSyncTree(diskState, diskState, loadout))
        {
            if (!node.HaveDisk) continue;

            var isDeployed = node.SourceItemType switch
            {
                LoadoutSourceItemType.Intrinsic => true,
                // A save on a protected path that the game rewrote is no longer what the loadout deployed.
                LoadoutSourceItemType.Loadout => node.Loadout.HaveEntityId
                    && !OverridesGroupLookup.IsInGroup(loadout.Db, node.Loadout.EntityId, overridesGroup)
                    && !(GamePathFilter.ShouldFilter(path) && node.Disk.Identity.MatchSameKind(node.Loadout.Identity) != IdentityMatch.Same),
                _ => false,
            };

            if (isDeployed) result.Add(path);
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlySet<GamePath> GetFilesDeployedByLastSyncedLoadout(GameInstallMetadata.ReadOnly metadata)
    {
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSynced)
            || !Loadout.Load(metadata.Db, lastSynced).IsValid())
        {
            // The helper would answer with an empty set, which a baseline build reads as "nothing deployed".
            throw new InvalidOperationException("The loadout last applied to this installation is gone, so the files it deployed are not known");
        }

        // The loadout as of the apply, not as it is now: a mod removed or disabled since still has its files on disk.
        var txId = GameInstallMetadata.LastSyncedLoadoutTransactionId.Get(metadata);
        var asOfApply = GameInstallMetadata.Load(metadata.Db.Connection.AsOf(TxId.From(txId.Value)), metadata.Id);
        return GetFilesDeployedByLastSyncedLoadout(asOfApply, GetDiskStateForGame(asOfApply));
    }
}
