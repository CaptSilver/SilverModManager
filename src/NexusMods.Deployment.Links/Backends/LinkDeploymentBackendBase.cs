using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Deployment.Links.Native;
using NexusMods.Deployment.Links.Probe;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Links.Backends;

/// <summary>
/// Deploys each file by linking it to, or cloning it from, the copy in the loose staging store, so a file
/// costs no extra bytes (reflink and hardlink) or only a directory entry (symlink). Everything that is not
/// moving file contents is the copy backend's.
/// </summary>
/// <remarks>
/// <para>
/// Content-addressing is what makes this risky: every deployed path with one hash can share one inode, and
/// a game that rewrites a file in place (Wine's CREATE_ALWAYS is an O_TRUNC write) would rewrite the store
/// file and every other path with it. The rules below keep that recoverable and attributable to one path.
/// </para>
/// <list type="bullet">
/// <item>A store file is linked from at most one path. Any further path with the same hash gets a private
/// copy, so an edit through a link changes one file and is reported as one external change. This holds
/// across applies, not just within one.</item>
/// <item>A deployed path is never opened for writing. An existing target is unlinked and then created
/// fresh, because truncating a path that is a hardlink to, or a symlink into, the store would truncate the
/// store file.</item>
/// <item>A store file that was already there is hashed before it is linked, cloned or copied anywhere. A
/// mismatch means a write went through an earlier link without the index noticing (it kept size and
/// modification time, or an edit was followed by a reset, which runs no ingest). The file is released and
/// extracted again, so damage never reaches a second path or a later apply.</item>
/// <item>The index compares what is on disk with what the last apply recorded. After an edit through a link
/// <c>node.Disk.Hash</c> is the new content, which has no store file; every question about the store file a
/// path was linked to is therefore asked of <c>node.Previous.Hash</c>.</item>
/// <item>An edit through a link is repaired when it is seen. Ingest runs before delete and extract (see the
/// order of <see cref="Actions"/>), so in a loadout apply a damaged store file is fixed before any extract
/// in the same apply can link it elsewhere. The reset path never ingests, so delete repairs too. A write
/// that keeps size and modification time is invisible to the index for the edited path, as it is with the
/// copy backend; the hash check above keeps it from spreading.</item>
/// </list>
/// <para>
/// The caller holds <see cref="ILooseStagingStore.Hold"/> for the whole apply, so the store is not swept
/// between ensuring a file and recording its link.
/// </para>
/// </remarks>
public abstract class LinkDeploymentBackendBase(CopyDeploymentBackend copy, ILooseStagingStore store, IGameRegistry gameRegistry, ILogger logger) : IDeploymentBackend
{
    /// <inheritdoc />
    public abstract DeploymentBackendId Id { get; }

    /// <inheritdoc />
    public bool UsesLooseStaging => true;

    /// <inheritdoc />
    public abstract DeploymentPreflight Preflight(GameInstallation installation, GamePath root);

    /// <inheritdoc />
    public abstract IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root);

    /// <summary>
    /// The staging store this backend links from.
    /// </summary>
    protected ILooseStagingStore Store => store;

    /// <summary>
    /// Creates <paramref name="target"/> as a link to, or clone of, <paramref name="storeFile"/>. The target
    /// does not exist, and an existing file must never be opened. Returns 0 or the errno.
    /// </summary>
    protected abstract int Place(AbsolutePath storeFile, AbsolutePath target);

    /// <summary>
    /// Whether <paramref name="deployed"/> is the staged file itself (same inode) or a symlink to it, which
    /// is to say whether writing to it writes the store file. Clones have no such identity.
    /// </summary>
    protected abstract bool HasLinkIdentity(AbsolutePath deployed, AbsolutePath storeFile);

    /// <summary>
    /// Whether one store file can end up behind more than one deployed path, which is what the
    /// one-link-per-file rule exists for.
    /// </summary>
    protected abstract bool SharesStoreFile { get; }

    /// <summary>
    /// Of the staged files, the hashes whose file is already linked from some deployed path outside the
    /// extraction in progress.
    /// </summary>
    protected abstract IReadOnlySet<Hash> HashesLinkedElsewhere(DeploymentContext context, IReadOnlyDictionary<Hash, AbsolutePath> storeFiles);

    /// <summary>
    /// Gives the store back a file it can trust after the deployed path <paramref name="deployed"/> was
    /// written through. The deployed path keeps the written bytes.
    /// </summary>
    protected abstract void RepairStoreAfterWriteThrough(GameInstallMetadataId installation, Hash previous, AbsolutePath deployed, AbsolutePath storeFile);

    /// <summary>
    /// Finds which staged file <paramref name="deployed"/> is a link to, by name, when that is not already
    /// known. <paramref name="storeInodes"/> maps the (device, inode) of every staged file to its hash and is
    /// only built when a backend asks for it.
    /// </summary>
    protected abstract bool TryFindLinkedStoreHash(GameInstallMetadataId installation, AbsolutePath deployed, Func<IReadOnlyDictionary<(ulong Device, ulong Inode), Hash>> storeInodes, out Hash hash);

    /// <summary>
    /// The installation's directory in the staging store, where the preflight probes write so they see the
    /// same filesystem as the staged files. Creates the installation's metadata on first use.
    /// </summary>
    protected AbsolutePath ProbeDirectory(GameInstallation installation) => store.DirectoryFor(gameRegistry.ForceGetMetadata(installation));

    /// <inheritdoc />
    public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => copy.Backup(context, nodes);

    /// <inheritdoc />
    public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => copy.AdaptLoadout(context, nodes);

    /// <inheritdoc />
    public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => copy.AddReifiedDeletes(context, nodes);

    /// <inheritdoc />
    public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState)
        => copy.CleanDirectories(context, directoriesWithDeletions, newDiskState);

    /// <inheritdoc />
    public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var storeInodes = StoreInodes(context);
        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.IngestFromDisk)) continue;

            var deployed = context.Locations.ToAbsolutePath(path);
            if (!TryFindDamagedStoreFile(context, deployed, node, storeInodes, out var damaged)) continue;

            logger.LogWarning("{Path} was written through its {Backend} link, repairing the staged file for {Hash}", path, Id, damaged);
            RepairStoreAfterWriteThrough(context.MetadataId, damaged, deployed, store.PathFor(context.MetadataId, damaged));
        }

        copy.Ingest(context, nodes);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        HashSet<GamePath> foldersWithDeletedFiles = [];
        var storeInodes = StoreInodes(context);
        var itemIndex = 0;
        var deleteFileCount = nodes.Sum(static x => x.Value.Actions.HasFlag(Actions.DeleteFromDisk) ? 1 : 0);

        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.DeleteFromDisk)) continue;

            context.Job?.ReportProgress(SyncPhases.Deleting, itemIndex, deleteFileCount);
            itemIndex++;

            var deployed = context.Locations.ToAbsolutePath(path);
            if (!IsOurs(context, deployed, node))
            {
                // Replaced after the index the tree was built from. The next index ingests it.
                logger.LogInformation("Leaving {Path} in place: it is no longer the file that was deployed", deployed);
                continue;
            }

            // The reset path never ingests, so a store file written through its link has to be repaired here
            // or the next Ensure would hand the damaged file out under its old name. The deployed file is
            // going away, so the written bytes only need the backup that already ran.
            if (TryFindDamagedStoreFile(context, deployed, node, storeInodes, out var damaged))
            {
                logger.LogWarning("{Path} was written through its {Backend} link, releasing the staged file for {Hash}", path, Id, damaged);
                store.Release(context.MetadataId, damaged);
            }

            File.Delete(deployed.ToString());

            // Only delete the entry if we're not going to replace it
            if (!node.Actions.HasFlag(Actions.ExtractToDisk))
            {
                foldersWithDeletedFiles.Add(path.Parent);
                DiskStateEntries.Retract(context.Transaction, context.MetadataId, path, node);
            }
        }

        context.Job?.ReportProgress(SyncPhases.Deleting, deleteFileCount, deleteFileCount);
        return foldersWithDeletedFiles;
    }

    /// <inheritdoc />
    public async Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var extract = nodes
            .Where(static x => x.Value.Actions.HasFlag(Actions.ExtractToDisk))
            .OrderBy(static x => x.Key.ToString(), StringComparer.Ordinal)
            .ToList();
        if (extract.Count == 0) return;

        logger.LogDebug("Linking {Count} files into the game folders with {Backend}", extract.Count, Id);

        var storeFiles = await EnsureVerified(context, extract.Select(static x => x.Value.Loadout.Hash).Distinct());

        // Unlink first: whatever is at a target may be a link to a staged file, which must neither be written
        // through nor counted as a link this apply still holds.
        foreach (var (path, _) in extract)
        {
            var target = context.Locations.ToAbsolutePath(path);
            target.Parent.CreateDirectory();
            File.Delete(target.ToString());
        }

        var linked = SharesStoreFile ? new HashSet<Hash>(HashesLinkedElsewhere(context, storeFiles)) : [];
        var itemIndex = 0;
        foreach (var (path, node) in extract)
        {
            context.Job?.ReportProgress(SyncPhases.Deploying, itemIndex++, extract.Count);

            var hash = node.Loadout.Hash;
            var storeFile = storeFiles[hash];
            var target = context.Locations.ToAbsolutePath(path);

            var privateCopy = SharesStoreFile && linked.Contains(hash);
            if (!privateCopy)
            {
                var errno = Place(storeFile, target);
                if (errno is LibcLinks.EXDEV or LibcLinks.EOPNOTSUPP)
                {
                    // Only a reset through a root that no longer accepts this backend gets here: an apply
                    // preflights its roots first. A copy completes the reset.
                    logger.LogWarning("{Backend} could not place {Target} ({Error}), writing a private copy instead", Id, target, ErrnoText.Describe(errno));
                    privateCopy = true;
                }
                else if (errno != 0)
                {
                    throw new IOException($"{Id} could not place '{target}' from '{storeFile}': {ErrnoText.Describe(errno)}");
                }
                else if (SharesStoreFile)
                {
                    linked.Add(hash);
                }
            }

            if (privateCopy) CopyToFreshFile(storeFile, target);

            ExecutableExtensions.ApplyTo(target);

            // The time the next index will read, which for a symlink is the staged file's.
            DeployedFileRows.Record(context.Transaction, context, path, node, FileFingerprint.Of(target).LastModified);
        }

        context.Job?.ReportProgress(SyncPhases.Deploying, extract.Count, extract.Count);
    }

    /// <inheritdoc />
    public async Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        // The copy backend replaces its target rather than truncating it, so a path that is still a link to a
        // staged file is safe, and an intrinsic file can read the previous version while it renders.
        await copy.WriteIntrinsics(context, nodes);
    }

    /// <summary>
    /// Stages every hash and returns the staged paths. A file that was staged before this call is hashed
    /// first, and a file whose bytes no longer match its name is released and staged again.
    /// </summary>
    private async Task<Dictionary<Hash, AbsolutePath>> EnsureVerified(DeploymentContext context, IEnumerable<Hash> hashes)
    {
        var installation = context.MetadataId;
        var storeFiles = new Dictionary<Hash, AbsolutePath>();
        var reused = new List<Hash>();
        foreach (var hash in hashes)
        {
            if (store.Contains(installation, hash)) reused.Add(hash);
            storeFiles[hash] = await store.Ensure(installation, hash);
        }

        // A file Ensure just extracted is trusted. Only one that was already there can have been written through.
        await Parallel.ForEachAsync(reused, async (hash, token) =>
        {
            var actual = await storeFiles[hash].XxHash3Async(token: token);
            if (actual == hash) return;

            logger.LogWarning(
                "Staged file {StoreFile} holds {Actual} instead of {Hash}, something wrote through a link to it. Staging it again",
                storeFiles[hash], actual, hash
            );
            store.Release(installation, hash);
            await store.Ensure(installation, hash, token);
        });

        return storeFiles;
    }

    /// <summary>
    /// Whether the deployed file is a link to a staged file whose name no longer matches the bytes, which is to
    /// say the staged file was written through the link. Returns the name (hash) of that staged file.
    /// </summary>
    private bool TryFindDamagedStoreFile(DeploymentContext context, AbsolutePath deployed, SyncNode node, Lazy<IReadOnlyDictionary<(ulong, ulong), Hash>> storeInodes, out Hash damaged)
    {
        damaged = default;
        if (!node.HaveDisk) return false;

        // A loadout apply: the previous state is the hash the last apply linked. The disk side is the content
        // now, which has no staged file, so only the previous hash can name the one that was written through.
        if (node.HavePrevious && node.Previous.Hash != node.Disk.Hash && HasLinkIdentity(deployed, store.PathFor(context.MetadataId, node.Previous.Hash)))
        {
            damaged = node.Previous.Hash;
            return true;
        }

        // A reset passes the current disk state as the previous one, so the link itself has to say which
        // staged file it leads to. A link to the file named for the content on disk is intact.
        if (HasLinkIdentity(deployed, store.PathFor(context.MetadataId, node.Disk.Hash))) return false;
        return TryFindLinkedStoreHash(context.MetadataId, deployed, () => storeInodes.Value, out damaged) && damaged != node.Disk.Hash;
    }

    private Lazy<IReadOnlyDictionary<(ulong, ulong), Hash>> StoreInodes(DeploymentContext context) => new(() =>
    {
        var inodes = new Dictionary<(ulong, ulong), Hash>();
        foreach (var (hash, path) in store.Enumerate(context.MetadataId))
        {
            if (LibcStat.TryStat(path.ToString(), out var stat) == 0) inodes[(stat.Device, stat.Inode)] = hash;
        }
        return inodes;
    });

    /// <summary>
    /// Whether the file at the deployed path is the one the last apply put there, so deleting it cannot
    /// take something the player or the game wrote since.
    /// </summary>
    private bool IsOurs(DeploymentContext context, AbsolutePath deployed, SyncNode node)
    {
        // Nothing there: nothing to take, and the row can still go.
        if (LibcStat.TryLStat(deployed.ToString(), out _) != 0) return true;

        if (node.HavePrevious && HasLinkIdentity(deployed, store.PathFor(context.MetadataId, node.Previous.Hash))) return true;

        // Private copies, generated files and a store path that is gone have no link identity, so the
        // fingerprint the index recorded is all there is to go on.
        try
        {
            var fingerprint = FileFingerprint.Of(deployed);
            return fingerprint.Size == node.Disk.Size && fingerprint.LastModified.UtcTicks == node.Disk.LastModifiedTicks;
        }
        catch (FileNotFoundException)
        {
            // A symlink whose target is gone and that is not ours has nothing left to compare.
            return false;
        }
    }

    /// <summary>
    /// Streams the staged file into a file that is created new. File.Copy would carry the staged inode's mode over.
    /// </summary>
    private static void CopyToFreshFile(AbsolutePath source, AbsolutePath target)
    {
        using var from = File.OpenRead(source.ToString());
        using var to = new FileStream(target.ToString(), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        from.CopyTo(to);
    }
}
