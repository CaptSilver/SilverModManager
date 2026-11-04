using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    /// <inheritdoc />
    public IReadOnlyList<UnwritableFile> FindUnwritableFiles(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousDiskState, IEnumerable<PathPartPair> lastScannedDiskState)
    {
        var syncTree = BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        ProcessSyncTree(syncTree);
        return UnwritableFiles.Of(syncTree);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs the backend steps an Apply would run for these files (back up and ingest for a file on disk, a recorded
    /// delete for one that is not) on a tree holding only them, and records nothing about an apply: the last applied
    /// loadout and the disk state it is compared with stay as they are, so a change somebody made to another file
    /// is still found by the next Apply.
    /// </remarks>
    public async Task<UnwritableResolutionResult> ResolveUnwritableFiles(Loadout.ReadOnly loadout, IReadOnlyCollection<GamePath>? paths, UnwritableResolution resolution, CancellationToken cancellationToken = default)
    {
        loadout = loadout.Rebase();
        var installation = loadout.InstallationInstance;
        var metadata = await ReindexState(installation, null, cancellationToken);
        var tree = BuildSyncTreeFromIndex(loadout, metadata);
        ProcessSyncTree(tree);

        var unwritable = tree.Where(kv => UnwritableFiles.Is(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var wanted = paths is null ? unwritable.Keys.ToList() : paths.Distinct().ToList();

        var chosen = new Dictionary<GamePath, SyncNode>();
        var skipped = new List<(GamePath Path, string Why)>();
        foreach (var path in wanted)
        {
            if (!unwritable.TryGetValue(path, out var node))
            {
                if (paths is not null) skipped.Add((path, "an Apply can write it, or the loadout does not name it"));
                continue;
            }

            var onDisk = UnwritableFiles.ReasonOf(node) == UnwritableReason.DifferentOnDisk;
            if (resolution == UnwritableResolution.KeepDiskFile && !onDisk)
            {
                if (paths is not null) skipped.Add((path, "it is not in the game folder, so there is no file to keep; forget it instead"));
                continue;
            }
            if (resolution == UnwritableResolution.Forget && onDisk)
            {
                if (paths is not null) skipped.Add((path, "the game folder has a file there, and forgetting it would have the next Apply delete that file; keep the file on disk instead"));
                continue;
            }

            chosen[path] = resolution == UnwritableResolution.KeepDiskFile
                ? await ForKeeping(node, installation, path, cancellationToken)
                : WithActions(node, Actions.AddReifiedDelete);
        }

        if (chosen.Count == 0)
        {
            var why = skipped.Count == 0 ? "No file is waiting on a copy that was never kept." : string.Join(" ", skipped.Select(s => $"`{s.Path.Path}`: {s.Why}."));
            throw new UnwritableResolutionRefusedException(why);
        }

        using (var _ = await _lock.LockAsync())
        {
            using var tx = Connection.BeginTransaction();
            var gameMetadataId = MetadataOf(loadout).GameInstallMetadataId;
            var plan = _planStore.EnsureComplete(tx, Connection.Db, installation, gameMetadataId, _backendSelector);
            var groups = GroupByBackend(plan, chosen);
            var context = new DeploymentContext
            {
                Synchronizer = this,
                Installation = installation,
                MetadataId = gameMetadataId,
                SyncTree = chosen,
                Loadout = loadout,
                Transaction = tx,
            };

            foreach (var group in groups)
            {
                if (resolution == UnwritableResolution.KeepDiskFile)
                {
                    await group.Backend.Backup(context, group.Nodes);
                    group.Backend.Ingest(context, group.Nodes);
                }
                else
                {
                    group.Backend.AddReifiedDeletes(context, group.Nodes);
                }
            }

            // With nothing left that an apply would leave out, the status can stop comparing on every look.
            if (unwritable.Keys.All(chosen.ContainsKey))
                tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastApplyLeftFilesUnextracted, false);

            await tx.Commit();
        }

        foreach (var path in chosen.Keys)
            Logger.LogInformation("Settled {Path} with {Resolution}: no copy of what the loadout wanted was kept", path, resolution);
        return new UnwritableResolutionResult(chosen.Keys.ToArray(), skipped);
    }

    private static SyncNode WithActions(SyncNode node, Actions actions)
    {
        node.Actions = actions;
        return node;
    }

    /// <summary>
    /// The node as the backup and ingest steps read it. The store is keyed by xxHash3, so a disk part named by another
    /// kind (a SHA-1 the store vouched for) is named by the hash of the bytes now on disk.
    /// </summary>
    private async Task<SyncNode> ForKeeping(SyncNode node, GameInstallation installation, GamePath path, CancellationToken cancellationToken)
    {
        var withActions = WithActions(node, Actions.BackupFile | Actions.IngestFromDisk);
        if (node.Disk.Identity.Kind == HashKind.XxHash3) return withActions;

        await using var stream = installation.Locations.ToAbsolutePath(path).Read();
        var hash = await Xx3Hasher.HashAsync(stream);
        withActions.Disk = new SyncNodePart
        {
            EntityId = node.Disk.EntityId,
            Identity = FileIdentity.XxHash3(hash),
            Size = node.Disk.Size,
            LastModifiedTicks = node.Disk.LastModifiedTicks,
        };
        return withActions;
    }
}
