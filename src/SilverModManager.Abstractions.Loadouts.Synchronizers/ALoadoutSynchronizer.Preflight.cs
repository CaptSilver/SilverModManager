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
    /// <summary>
    /// Lets a game refuse to apply a loadout before anything is written. It runs at the start of
    /// <see cref="Synchronize"/>, ahead of the locator id update and the re-index (both write to the database)
    /// and ahead of the loadout switch, which would otherwise deactivate the previous loadout and reset the game
    /// before <see cref="PreflightActions"/> got the chance to refuse. Throw to refuse.
    /// </summary>
    protected virtual Task PreflightLoadout(Loadout.ReadOnly loadout) => Task.CompletedTask;

    /// <summary>
    /// Lets a game refuse a set of actions before the first one runs, for example when it cannot produce a
    /// generated file. Runs in both <see cref="RunActions(Dictionary{GamePath, SyncNode}, Loadout.ReadOnly, SynchronizeLoadoutJob?)"/>
    /// and the game installation overload (which passes no loadout), after the never-written paths are taken
    /// off the tree and before any write. An exception propagates and leaves disk and database untouched.
    /// </summary>
    protected virtual Task PreflightActions(Dictionary<GamePath, SyncNode> tree, Loadout.ReadOnly? loadout) => Task.CompletedTask;

    /// <summary>
    /// What both RunActions overloads check, in this order, after the tree is respelled and before the lock is taken.
    /// A reset gets the extraction check too: its bytes can leave the store between building the reset tree and
    /// running it (an archive removed), and since the delete phase runs before the extract phase, the store's error
    /// would otherwise come after files were already deleted.
    /// </summary>
    private async Task PreflightTree(Dictionary<GamePath, SyncNode> syncTree, GameInstallation installation, Loadout.ReadOnly? loadout, BackupOverflowChoice choice)
    {
        RefuseTraversalPaths(syncTree);
        KeepNeverWrittenPathsOffDisk(syncTree);
        await PreflightActions(syncTree, loadout);
        await RefuseExtractionsWithoutBytes(syncTree, forReset: loadout is null);
        ProbeWritableRoots(syncTree, installation.Locations);
        ApplyBackupOverflowChoice(syncTree, choice);
    }

    /// <summary>
    /// Refuses the apply when a root recorded with a backend other than copy no longer accepts it, before
    /// any file is written. The game having moved to another drive, or the staging store having moved, would
    /// otherwise surface halfway through extraction as a failed link.
    /// </summary>
    /// <remarks>
    /// Only roots the tree writes into are checked, so a sync with nothing to write touches no disk, as in
    /// <see cref="ProbeWritableRoots"/>. Deleting works whatever the backend can no longer do.
    /// </remarks>
    private void PreflightRecordedRoots(GameInstallation installation, DeploymentPlan plan, Dictionary<GamePath, SyncNode> syncTree)
    {
        if (RecordedRootPreflight.IsSuspended) return;

        const Actions writes = Actions.ExtractToDisk | Actions.WriteIntrinsic;
        foreach (var root in plan.Roots)
        {
            if (root.Backend == DeploymentBackendId.Copy) continue;
            if (!syncTree.Any(x => (x.Value.Actions & writes) != 0 && plan.BackendFor(x.Key) == root.Backend && IsInside(root.Root, x.Key))) continue;

            var preflight = _backends[root.Backend].Preflight(installation, root.Root);
            if (preflight.Accepted) continue;

            // --recommended only moves roots nobody picked, and a root recorded by hand stays put, so the message names the
            // move that does work for this one.
            var rootName = DeploymentRootNames.Describe(root.Root);
            var pick = _backendSelector.Choose(installation, root.Root).Backend;
            throw new SynchronizerPreflightException(
                $"{installation.Game.DisplayName} is set to deploy '{rootName}' with {root.Backend}, which does not work there any more: {preflight.Reason}. " +
                $"Nothing was changed. Run 'silvermodmanager deployment --game \"{installation.Game.DisplayName}\" --root {rootName} --use {pick.Value}' to move it, then apply again."
            );
        }

        static bool IsInside(GamePath root, GamePath path) =>
            root.LocationId == path.LocationId && (root.Path == RelativePath.Empty || path.Path == root.Path || path.Path.InFolder(root.Path));
    }

    /// <inheritdoc />
    public Task Preflight(GameInstallation installation)
    {
        // Only the prefix path is checked, never Steam's configuration: guessing another prefix would write
        // into a folder the player did not mean, so an unclear case is a message instead.
        if (installation.LocatorResult.WinePrefixPath is { } prefix && !ProtonPrefix.IsReady(prefix))
            return Task.FromException(new SynchronizerPreflightException(ProtonMessages.MissingPrefix(installation.Game.DisplayName, prefix)));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks that every location the tree is about to write into can be written, before anything is.
    /// </summary>
    /// <remarks>
    /// A write that fails halfway leaves some files extracted and the database not yet updated, which the next
    /// sync reads as external changes. A tree without write actions probes nothing, so a no-op sync never
    /// touches the disk or bumps a folder's modification time. Locations the filter rejects are skipped:
    /// the synchronizer never writes there.
    /// </remarks>
    private void ProbeWritableRoots(Dictionary<GamePath, SyncNode> syncTree, GameLocations locations)
    {
        const Actions writes = Actions.ExtractToDisk | Actions.WriteIntrinsic | Actions.DeleteFromDisk;

        HashSet<LocationId> probed = [];
        foreach (var (path, node) in syncTree)
        {
            if ((node.Actions & writes) == 0) continue;
            if (!probed.Add(path.LocationId)) continue;
            if (GamePathFilter.ShouldFilter(new GamePath(path.LocationId, RelativePath.Empty))) continue;

            // A root that does not exist yet (My Games/<game>) is created by the first extract, so what has to
            // be writable is the closest folder that does exist.
            var folder = locations[path.LocationId].Path;
            while (!folder.DirectoryExists() && folder.Parent != folder) folder = folder.Parent;

            var probe = folder.Combine(ProtonMessages.ProbeFileName);
            try
            {
                probe.Create().Dispose();
                probe.Delete();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                throw new SynchronizerPreflightException(ProtonMessages.UnwritableFolder(folder));
            }
        }
    }

    /// <summary>
    /// Whether the synchronizer must leave the file at the given path exactly as it is: no write, delete, backup or
    /// ingest, whichever entry point runs the actions.
    /// </summary>
    /// <remarks>
    /// A loadout file that would be deployed to such a path makes the sync fail before anything runs. Every other
    /// action on the path is dropped from the sync tree, which leaves the disk state as indexed.
    /// <see cref="IsIgnoredBackupPath"/> is no substitute: it only decides whether a file is backed up, a new file
    /// on an ignored path is still ingested into the loadout.
    /// </remarks>
    protected virtual bool IsNeverWritten(GamePath path) => false;

    private const Actions DiskAndLoadoutWrites = Actions.BackupFile | Actions.IngestFromDisk | Actions.AdaptLoadout | Actions.DeleteFromDisk | Actions.ExtractToDisk;

    private void KeepNeverWrittenPathsOffDisk(Dictionary<GamePath, SyncNode> syncTree)
    {
        List<GamePath>? cleared = null;

        foreach (var path in syncTree.Keys.ToArray())
        {
            if (!IsNeverWritten(path)) continue;

            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(syncTree, path);
            var fromLoadoutItem = node.SourceItemType is LoadoutSourceItemType.Loadout or LoadoutSourceItemType.Intrinsic;
            if (fromLoadoutItem && (node.Actions & (Actions.ExtractToDisk | Actions.WriteIntrinsic | Actions.WarnOfUnableToExtract)) != 0)
                throw new SynchronizerPreflightException(NeverWrittenMessage(path, node));

            if ((node.Actions & DiskAndLoadoutWrites) == 0) continue;

            var remaining = node.Actions & ~DiskAndLoadoutWrites;
            // The warning belongs to the extract that was just dropped.
            remaining &= ~Actions.WarnOfUnableToExtract;
            node.Actions = remaining == 0 ? Actions.DoNothing : remaining;
            (cleared ??= []).Add(path);
        }

        if (cleared is null) return;
        const int listed = 10;
        Logger.LogInformation("Leaving {Count} path(s) the synchronizer never writes as they are: {Paths}{More}",
            cleared.Count, string.Join(", ", cleared.Take(listed)), cleared.Count > listed ? $" and {cleared.Count - listed} more" : "");
    }

    /// <summary>
    /// Whether the path could address something outside its location: a "." or ".." segment, a segment that is
    /// a drive, or a rooted path. RelativePath keeps "." and ".." as written and accepts ':' inside a path, and
    /// the paths are joined onto a location folder when written, so a mod could otherwise place files anywhere
    /// the player can write.
    /// </summary>
    internal static bool IsTraversalPath(GamePath path)
    {
        var text = path.Path.ToString();
        if (text.StartsWith('/') || text.StartsWith('\\')) return true;

        foreach (var segment in text.Split('/', '\\'))
        {
            if (segment is "." or ".." || segment.Contains(':')) return true;
        }
        return false;
    }

    private void RefuseTraversalPaths(Dictionary<GamePath, SyncNode> syncTree)
    {
        foreach (var (path, node) in syncTree)
        {
            if (node.SourceItemType is not (LoadoutSourceItemType.Loadout or LoadoutSourceItemType.Intrinsic)) continue;
            if (!IsTraversalPath(path)) continue;

            throw new SynchronizerPreflightException(
                $"Cannot apply: {DescribeProvider(node)} provides '{path.Path}', whose '.', '..' or drive segment could reach outside the {path.LocationId} location. " +
                "Every sync fails until that mod is removed from the loadout.");
        }
    }

    private string DescribeProvider(SyncNode node)
    {
        var provider = "an intrinsic file of the game";
        if (node.HaveLoadout && node.Loadout.HaveEntityId)
        {
            var item = LoadoutItem.Load(Connection.Db, node.Loadout.EntityId);
            if (item.IsValid() && item.HasParent()) provider = $"the mod '{item.Parent.AsLoadoutItem().Name}'";
        }
        return provider;
    }

    private string NeverWrittenMessage(GamePath path, SyncNode node)
    {
        var provider = DescribeProvider(node);

        return $"Cannot apply: {provider} provides '{path.Path}', but files in the {path.LocationId} location are never written for this game. " +
               "Every sync fails until that mod is removed from the loadout.";
    }
}
