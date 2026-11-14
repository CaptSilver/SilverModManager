using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Deployment.Fuse.Availability;
using SilverModManager.Deployment.Fuse.Kernel;
using SilverModManager.Deployment.Fuse.Mount;
using SilverModManager.Deployment.Vfs;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Deployment.Fuse.Backend;

/// <summary>
/// Serves a game's data folder through a FUSE mount instead of writing files into it. An apply records what the mount
/// will serve as <see cref="MountedFileEntry"/> rows and touches nothing under the root; the synchronizer lays those
/// rows over the folder's real state when it builds its tree (see <see cref="MountManifestView"/>).
/// </summary>
/// <remarks>
/// Files a mod shadows keep their real copy in the folder. The row remembers which real file it covers, so a change
/// made to that file from outside (a tool run without the app) is seen as one and ingested instead of being served over.
/// </remarks>
public sealed class FuseDeploymentBackend(
    CopyDeploymentBackend copy,
    ILooseStagingStore store,
    IGameRegistry games,
    IFuseKernel kernel,
    IBackingScanner scanner,
    VfsSessionFactory sessions,
    ISettingsManager settings,
    IFuseEnvironmentProbe probe,
    IDeploymentPlanStore plans,
    IConnection connection,
    IFuseUnmounter unmounter,
    StagingPromoterFactory promoters,
    IServiceProvider services,
    ILogger<FuseDeploymentBackend> logger) : IMountingDeploymentBackend
{
    // FuseMountOptions.Subtype, which the kernel lists as fuse.<subtype>. Recovery finds a dead session's mount by it.
    private const string FileSystemType = "fuse.silvermod";

    /// <summary>How long a finishing mount waits for its session loop to end before it leaves the staged files for recovery.</summary>
    internal TimeSpan CompletionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public DeploymentBackendId Id => DeploymentBackendId.Fuse;

    /// <inheritdoc />
    public bool UsesLooseStaging => true;

    /// <inheritdoc />
    public DeploymentPreflight Preflight(GameInstallation installation, GamePath root)
    {
        var db = connection.Db;
        var plan = DeploymentPlan.Empty;
        IReadOnlyCollection<GamePath> intrinsics = [];
        if (games.TryGetMetadata(installation, out var metadata))
        {
            plan = plans.Load(db, metadata.GameInstallMetadataId);
            intrinsics = IntrinsicPaths(installation, db, metadata.GameInstallMetadataId);
        }

        return FusePreflight.Evaluate(installation, root, plan, FuseAvailability.Evaluate(probe), mountRoot => IsMounted(installation, mountRoot), intrinsics);
    }

    // Every loadout of the installation, not just the active one: switching to another must not be what breaks a mount.
    private static List<GamePath> IntrinsicPaths(GameInstallation installation, IDb db, GameInstallMetadataId id)
    {
        var paths = new List<GamePath>();
        if (installation.Game is not IHasSynchronizer { Synchronizer: ALoadoutSynchronizer synchronizer }) return paths;

        foreach (var loadout in Loadout.All(db).Where(l => l.InstallationId.Value == id.Value))
            paths.AddRange(synchronizer.IntrinsicFiles(loadout).Keys);
        return paths;
    }

    /// <inheritdoc />
    public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root)
    {
        var folder = installation.Locations.ToAbsolutePath(root);
        return [FusePreflight.RealDirectory(folder) ?? folder];
    }

    /// <inheritdoc />
    public bool IsMounted(GameInstallation installation, GamePath root)
    {
        var folder = installation.Locations.ToAbsolutePath(root);
        // The lookup resolves the folder's parent, and nothing is mounted under one that is not there.
        if (!folder.Parent.DirectoryExists()) return false;

        var (parent, leaf) = MountTable.SplitForLookup(FusePreflight.RealDirectory(folder)?.ToString() ?? folder.ToString());
        return MountTable.IsMounted(parent, leaf, FileSystemType);
    }

    /// <inheritdoc />
    public MountManifestView Manifest(GameInstallMetadataId installation, GamePath root, IDb db)
    {
        var rows = new List<MountManifestRow>();
        foreach (var entry in MountedFileEntry.FindByInstallation(db, installation))
        {
            if (entry.Root != root) continue;
            rows.Add(new MountManifestRow(
                entry.Path,
                entry.Identity.HasValue ? entry.Identity.Value : FileIdentity.None,
                entry.Size.HasValue ? entry.Size.Value : Size.Zero,
                entry.BackingIdentity.HasValue ? entry.BackingIdentity.Value : null,
                entry.Hidden is { HasValue: true, Value: true },
                entry.MTime.HasValue ? entry.MTime.Value : null));
        }
        return new MountManifestView(root, rows);
    }

    /// <inheritdoc />
    public async Task<IDeploymentMount> Mount(GameInstallation installation, Loadout.ReadOnly loadout, GamePath root, CancellationToken token)
    {
        if (!games.TryGetMetadata(installation, out var metadata))
            throw new InvalidOperationException($"{installation.Game.DisplayName} is not a managed installation, so there is nothing to mount");
        if (installation.Game is not IHasSynchronizer { Synchronizer: ALoadoutSynchronizer synchronizer })
            throw new InvalidOperationException($"{installation.Game.DisplayName} has no synchronizer to say which paths the loadout deletes");

        var id = metadata.GameInstallMetadataId;
        var folder = installation.Locations.ToAbsolutePath(root);
        var mountPoint = FusePreflight.RealDirectory(folder) ?? throw new DirectoryNotFoundException($"{folder} does not exist");

        var manifest = Manifest(id, root, connection.Db);
        foreach (var identity in manifest.Rows.Where(static r => !r.Hidden && !r.ServesTheRealFile).Select(static r => r.Identity).Distinct())
            await store.Ensure(id, identity, token);

        // The folder is opened before the mount covers its path and read only through this handle from here on.
        var backing = DirectoryHandle.Open(mountPoint);
        try
        {
            var entries = scanner.Scan(backing);
            var sources = MountSourceBuilder.Build(manifest, synchronizer.DeletedWinners(loadout), identity => store.PathFor(id, identity));
            var tree = VfsTree.Build(backing, entries, sources, sessions.StagingFor(id), settings.Get<VfsSettings>().CachePolicy);
            var session = sessions.Create(id, loadout.LoadoutId, root, tree);
            var mount = await kernel.Mount(tree, FuseMountOptions.Default(mountPoint), token);
            return new FuseMount(mount, session, backing, root, CompletionTimeout, logger);
        }
        catch
        {
            backing.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MountRecoveryReport> Recover(GameInstallation installation, GamePath root, LoadoutId? target, CancellationToken token)
    {
        if (!games.TryGetMetadata(installation, out var metadata)) return new MountRecoveryReport(MountRecoveryStatus.Nothing, WasMounted: false);

        var id = metadata.GameInstallMetadataId;
        var folder = installation.Locations.ToAbsolutePath(root);
        var mountPoint = FusePreflight.RealDirectory(folder) ?? folder;

        // The tracker is looked up here: the data model builds this backend in hosts that never launch a game and so
        // never register one.
        var recovery = new MountRecovery(services.GetRequiredService<IProcessTracker>(), unmounter, logger);

        // The dead mount is what the guard refuses on, and the work below is what removes it.
        using var _ = StaleMountGuard.Suspend();
        return await recovery.Run(installation, mountPoint, IsMounted(installation, root), sessions.StagingFor(id), promoters.For(id, root), target, token);
    }

    /// <inheritdoc />
    public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => copy.Backup(context, nodes);

    /// <inheritdoc />
    public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        copy.Ingest(context, nodes);

        // The row that covered this path was dropped from the overlay because the real file changed, which is why the
        // path is being ingested. Leaving the row would serve the old mod bytes over the ingested file at the next mount.
        var rows = ExistingRows(context);
        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.IngestFromDisk)) continue;
            if (rows.TryGetValue(path, out var row)) context.Transaction.Delete(row.Id, recursive: false);
        }
    }

    /// <inheritdoc />
    public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => copy.AdaptLoadout(context, nodes);

    /// <inheritdoc />
    public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => copy.AddReifiedDeletes(context, nodes);

    /// <inheritdoc />
    public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => copy.WriteIntrinsics(context, nodes);

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is deleted on disk. A row with no loadout file behind it is retracted, and so is the real file it covered
    /// when the loadout deletes the path: that becomes a hidden row, because the real file stays in the folder.
    /// </remarks>
    public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var tx = context.Transaction;
        var rows = ExistingRows(context);
        var fuseRoots = FuseRoots(context);
        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.DeleteFromDisk)) continue;
            // The extraction that replaces the file records its own row.
            if (node.Actions.HasFlag(Actions.ExtractToDisk)) continue;

            if (rows.TryGetValue(path, out var row))
            {
                tx.Delete(row.Id, recursive: false);
                if (row.BackingIdentity.HasValue) WriteHidden(context, RootOf(fuseRoots, context, path), path, row.BackingIdentity.Value);
            }
            else if (node.HaveDisk)
            {
                WriteHidden(context, RootOf(fuseRoots, context, path), path, node.Disk.Identity);
            }
        }

        return [];
    }

    /// <inheritdoc />
    public async Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var extract = nodes.Where(static n => n.Value.Actions.HasFlag(Actions.ExtractToDisk)).OrderBy(static n => n.Key.ToString(), StringComparer.Ordinal).ToList();
        if (extract.Count == 0) return;

        var staged = extract.Where(static n => n.Value.SourceItemType == LoadoutSourceItemType.Loadout).Select(static n => n.Value.Loadout.Identity).Distinct().ToList();
        await StagedFiles.EnsureVerified(context, store, staged, logger);

        var tx = context.Transaction;
        var rows = ExistingRows(context);
        var fuseRoots = FuseRoots(context);
        var index = 0;
        foreach (var (path, node) in extract)
        {
            context.Job?.ReportProgress(SyncPhases.Deploying, index++, extract.Count);
            rows.TryGetValue(path, out var existing);

            // The vanilla file is the winner again: the real file serves it.
            if (node.SourceItemType == LoadoutSourceItemType.Game)
            {
                if (existing.IsValid()) tx.Delete(existing.Id, recursive: false);
                continue;
            }

            if (existing.IsValid()) tx.Delete(existing.Id, recursive: false);

            // The real file already holds these bytes, so the folder serves them without a row, unless the game wants a
            // modification time the real file does not have: the row is what carries it.
            var backing = BackingOf(existing, node);
            DateTimeOffset? planned = context.PlannedModifiedTimes.TryGetValue(path, out var time) ? time : null;
            if (backing is { } covered && covered.Equals(node.Loadout.Identity) && planned is null) continue;

            _ = new MountedFileEntry.New(tx)
            {
                InstallationId = context.MetadataId,
                Root = RootOf(fuseRoots, context, path).ToGamePathParentTuple(context.MetadataId),
                Path = path.ToGamePathParentTuple(context.MetadataId),
                Identity = node.Loadout.Identity,
                Size = node.Loadout.Size,
                BackingIdentity = backing,
                MTime = planned,
            };
        }

        context.Job?.ReportProgress(SyncPhases.Deploying, extract.Count, extract.Count);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The time goes on the file's row, since the real file is not touched. A file the real folder holds with no row
    /// over it gets one whose identity is its backing identity: it carries only the time, and the mount serves the real
    /// file under it (<see cref="MountManifestRow.ServesTheRealFile"/>).
    /// </remarks>
    public Task StampTimestamps(DeploymentContext context, IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> stamps)
    {
        var tx = context.Transaction;
        var rows = ExistingRows(context);
        var fuseRoots = FuseRoots(context);
        foreach (var (path, mtime) in stamps)
        {
            if (!PlannedTimestamps.TryGetStampable(context, path, out var node)) continue;

            if (rows.TryGetValue(path, out var row))
            {
                if (!row.MTime.HasValue || row.MTime.Value.UtcTicks != mtime.UtcTicks) tx.Add(row.Id, MountedFileEntry.MTime, mtime);
                continue;
            }

            _ = new MountedFileEntry.New(tx)
            {
                InstallationId = context.MetadataId,
                Root = RootOf(fuseRoots, context, path).ToGamePathParentTuple(context.MetadataId),
                Path = path.ToGamePathParentTuple(context.MetadataId),
                Identity = node.Disk.Identity,
                Size = node.Disk.Size,
                BackingIdentity = node.Disk.Identity,
                MTime = mtime,
            };
        }
        return Task.CompletedTask;
    }

    // What the row under this path covers. A row already there keeps what it covered (a mod replacing another mod leaves
    // the vanilla file underneath), unless the disk side is a different file than the row served: then the real file
    // changed since and it is the one now being covered.
    private static FileIdentity? BackingOf(MountedFileEntry.ReadOnly existing, SyncNode node)
    {
        var served = existing.IsValid() && existing.Identity.HasValue ? existing.Identity.Value : FileIdentity.None;
        if (existing.IsValid() && (!node.HaveDisk || node.Disk.Identity.Equals(served)))
            return existing.BackingIdentity.HasValue ? existing.BackingIdentity.Value : null;
        return node.HaveDisk ? node.Disk.Identity : null;
    }

    /// <inheritdoc />
    public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState)
    {
        // Nothing was deleted on disk, so no folder was emptied.
    }

    private static void WriteHidden(DeploymentContext context, GamePath root, GamePath path, FileIdentity covered)
    {
        _ = new MountedFileEntry.New(context.Transaction)
        {
            InstallationId = context.MetadataId,
            Root = root.ToGamePathParentTuple(context.MetadataId),
            Path = path.ToGamePathParentTuple(context.MetadataId),
            BackingIdentity = covered,
            Hidden = true,
        };
    }

    private Dictionary<GamePath, MountedFileEntry.ReadOnly> ExistingRows(DeploymentContext context)
    {
        var rows = new Dictionary<GamePath, MountedFileEntry.ReadOnly>();
        foreach (var entry in MountedFileEntry.FindByInstallation(connection.Db, context.MetadataId)) rows[entry.Path] = entry;
        return rows;
    }

    private List<GamePath> FuseRoots(DeploymentContext context) =>
        plans.Load(connection.Db, context.MetadataId).Roots.Where(r => r.Backend == Id).Select(r => r.Root).ToList();

    // The mount root a path belongs to: the fuse root of the plan that holds it.
    private static GamePath RootOf(List<GamePath> fuseRoots, DeploymentContext context, GamePath path)
    {
        foreach (var root in fuseRoots)
        {
            if (path.InFolder(root)) return root;
        }
        throw new InvalidOperationException($"{path} was routed to the fuse backend, but no fuse root of {context.Installation.Game.DisplayName} holds it");
    }
}
