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
    public async ValueTask BuildProcessRun(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly state, CancellationToken cancellationToken, BackupOverflowChoice? largeFiles = null)
    {
        // Folders that differ only in case are merged and the recorded paths respelled before the loadout is read: a switch
        // to a loadout authored on Windows would otherwise deploy both spellings. The caller's state predates the merge, so
        // the loadout and the state are read again from the database when it changed anything.
        var installation = loadout.InstallationInstance;
        var folded = MergesCaseVariantFoldersOnApply(installation);
        if (folded)
        {
            state = await FoldAndReindex(installation, loadout.Id, null, alwaysReindex: false, cancellationToken);
            loadout = loadout.Rebase();
        }

        try
        {
            await BuildProcessRunCore(loadout, state, cancellationToken, largeFiles);
        }
        finally
        {
            _applySpellings = null;
        }
    }

    private async ValueTask BuildProcessRunCore(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly state, CancellationToken cancellationToken, BackupOverflowChoice? largeFiles)
    {
        // The loadout-switch path reaches the hook only here: Synchronize hands the switch to the loadout
        // manager, which ends in this method.
        loadout = await ReconcileGameInstalledContent(loadout, state);
        loadout = await PrepareLoadoutForSync(loadout);

        // The preparation can respell recorded disk state, so the tree reads the state it left, not the caller's snapshot.
        state = GameInstallMetadata.Load(Connection.Db, state.Id);
        var diskStateEntries = DiskStateEntry.FindByGame(state.Db, state);
        var diskState = DiskStateToPathPartPair(diskStateEntries).ToList();
        var tree = BuildSyncTree(diskState, diskState, loadout);
        AdoptUserFilesOutsideGame(tree, loadout, state, diskState);
        ProcessSyncTree(tree);

        var (choice, _) = await ResolveBackupOverflow(tree, loadout.InstallationInstance, largeFiles, cancellationToken, operation: BackupOverflowOperation.Switch);
        await RunActions(tree, loadout, choice: choice);
    }

    /// <summary>
    /// Lets the incoming loadout adopt a file outside <see cref="LocationId.Game"/> that no loadout accounts for,
    /// so a loadout switch keeps it.
    /// </summary>
    /// <remarks>
    /// A switch builds the tree with the disk as both the current and the previous state, so a user's file in
    /// Preferences (a game's own INI, say) that the incoming loadout lacks reads as "was there, still is, the
    /// loadout dropped it" and is deleted. Clearing its previous state makes it read as a file that appeared on
    /// disk, which the loadout ingests the way any apply ingests a new file. Faking a desired state equal to the
    /// disk, as a reset does, is not enough here: the switch records the incoming loadout as applied, and its
    /// next apply would delete the file all the same.
    /// Left alone: the incoming loadout's own files and generated files, files the last synced loadout deployed
    /// (they go with it), paths the game's filter protects (saves are never ingested), and paths the incoming
    /// loadout marks deleted.
    /// </remarks>
    private void AdoptUserFilesOutsideGame(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly metadata, List<PathPartPair> diskState)
    {
        HashSet<GamePath>? keepAsIs = null;
        foreach (var (path, _) in diskState)
        {
            if (path.LocationId == LocationId.Game || GamePathFilter.ShouldFilter(path)) continue;

            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            if (Unsafe.IsNullRef(ref node) || node.HaveLoadout || node.SourceItemType == LoadoutSourceItemType.Intrinsic) continue;

            if (keepAsIs is null)
            {
                keepAsIs = GetFilesDeployedByLastSyncedLoadout(metadata, diskState);
                keepAsIs.UnionWith(DeletedWinners(loadout));
            }
            if (keepAsIs.Contains(path)) continue;

            node.Previous = default;
        }
    }

    /// <summary>
    /// The paths an Apply wrote, deleted or took over from the disk, which is what a cache derived from the game
    /// files has to be checked against.
    /// </summary>
    private static List<GamePath> ChangedGamePaths(Dictionary<GamePath, SyncNode> syncTree)
        => syncTree.Where(pair => (pair.Value.Actions & (Actions.ExtractToDisk | Actions.DeleteFromDisk | Actions.IngestFromDisk)) != 0)
            .Select(pair => pair.Key)
            .ToList();

    /// <summary>
    /// Whether the path is a shader cache that Proton (vkd3d-proton, DXVK) drops next to the running game.
    /// </summary>
    /// <remarks>
    /// Proton writes these into the game's working directory, so they show up in <see cref="LocationId.Game"/>
    /// or wherever the game runs from. They change on every launch, so indexing them would report an external
    /// change and back up a new copy after each play session. The match is on the file name alone, so it holds
    /// for any location.
    /// </remarks>
    private static bool IsSharedProtonCache(GamePath path)
    {
        var name = path.FileName;
        return name.ToString().StartsWith("vkd3d-proton.cache", StringComparison.Ordinal)
               || name.Extension == DxvkCacheExtension;
    }

    private static readonly Extension DxvkCacheExtension = new(".dxvk-cache");

    /// <summary>
    /// Whether the path is a file <see cref="ProbeWritableRoots"/> or a link backend's preflight creates (the
    /// latter add a suffix to the name). They are deleted again right away, but a crash in between would
    /// leave one behind, and it must never be indexed as the player's file.
    /// </summary>
    private static bool IsWriteProbe(GamePath path) => path.FileName.ToString().StartsWith(ProtonMessages.ProbeFileName, StringComparison.Ordinal);
}
