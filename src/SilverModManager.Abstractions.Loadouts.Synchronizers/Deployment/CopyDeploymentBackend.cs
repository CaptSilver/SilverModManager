using System.Collections.Concurrent;
using System.Diagnostics;
using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Deploys by copying files out of the file store into the game folders, and reads changes back
/// by hashing what is there.
/// </summary>
public sealed class CopyDeploymentBackend : IDeploymentBackend
{
    private readonly IFileStore _fileStore;
    private readonly IConnection _connection;
    private readonly ILogger<CopyDeploymentBackend> _logger;

    public CopyDeploymentBackend(IFileStore fileStore, IConnection connection, ILogger<CopyDeploymentBackend> logger)
    {
        _fileStore = fileStore;
        _connection = connection;
        _logger = logger;
    }

    /// <inheritdoc />
    public DeploymentBackendId Id => DeploymentBackendId.Copy;

    /// <inheritdoc />
    public bool UsesLooseStaging => false;

    /// <inheritdoc />
    public DeploymentPreflight Preflight(GameInstallation installation, GamePath root) => DeploymentPreflight.Accept("writes plain files");

    /// <inheritdoc />
    public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

    private static Loadout.ReadOnly RequireLoadout(DeploymentContext context, [System.Runtime.CompilerServices.CallerMemberName] string? phase = null)
    {
        return context.Loadout ?? throw new InvalidOperationException($"{phase} needs a loadout, but the context has none");
    }

    /// <inheritdoc />
    public async Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var installation = context.Installation;
        var installMetadataId = context.MetadataId;
        // During ingest, new files that haven't been seen before are fed into the game's synchronizer to convert a
        // DiskStateEntry (hash, size, path) into some sort of LoadoutItem. By default, these are converted into a "LoadoutFile".
        // All Loadoutfile does, is say that this file is copied from the downloaded archives, that is, it's not generated
        // by any extension system.
        //
        // So the problem is, the ingest process has tagged all these new files as coming from the downloads, but likely
        // they've never actually been copied/compressed into the download folders. So if we need to restore them they won't exist.
        //
        // If a game wants other types of files to be backed up, they could do so with their own logic. But backing up a
        // IGeneratedFile is pointless, since when it comes time to restore that file we'll call file.Generate on it since
        // it's a generated file.

        // TODO: This may be slow for very large games when other games/mods already exist.
        // Backup the files that are new or changed
        var archivedFiles = new ConcurrentBag<ArchivedFileEntry>();
        var pinnedFileHashes = new ConcurrentBag<Hash>();

        var backupNodes = nodes.Where(static item => item.Value.Actions.HasFlag(Actions.BackupFile)).ToList();
        long filesChecked = 0;
        var totalFiles = backupNodes.Count;
        var totalBytes = backupNodes.Sum(static item => (long)item.Value.Disk.Size.Value);
        context.Job?.ReportProgress(SyncPhases.BackingUp, 0, totalFiles, 0, totalBytes);
        // Bytes only advance once a file is archived, so a file already in the store counts as done right away.
        long bytesAccountedFor = 0;

        await Parallel.ForEachAsync(backupNodes, async (item, _) =>
            {
                var (gamePath, node) = item;

                var path = installation.Locations.ToAbsolutePath(gamePath);
                Debug.Assert(node.HaveDisk, "Node must have a disk entry to backup");

                if (await _fileStore.HaveFile(node.Disk.Hash))
                {
                    context.Job?.ReportProgress(SyncPhases.BackingUp, Interlocked.Increment(ref filesChecked), totalFiles, Interlocked.Add(ref bytesAccountedFor, (long)node.Disk.Size.Value), totalBytes);
                    return;
                }
                
                var archivedFile = new ArchivedFileEntry
                {
                    Size = node.Disk.Size,
                    Hash = node.Disk.Hash,
                    StreamFactory = new NativeFileStreamFactory(path),
                };

                archivedFiles.Add(archivedFile);
                
                // TODO: We should only pin game files, not override files as well.
                // This check does not work as intended because the winning files is going to be a Loadout one, not a game one.
                // if (node.SourceItemType == LoadoutSourceItemType.Game)
                pinnedFileHashes.Add(archivedFile.Hash);
                context.Job?.ReportProgress(SyncPhases.BackingUp, Interlocked.Increment(ref filesChecked), totalFiles, Interlocked.Read(ref bytesAccountedFor), totalBytes);
            }
        );

        // PERFORMANCE: We deduplicate above with the HaveFile call.
        // The archive is written in one pass that reports nothing, so the bytes it covers are counted when it is done.
        await _fileStore.BackupFiles(archivedFiles, deduplicate: false);
        context.Job?.ReportProgress(SyncPhases.BackingUp, totalFiles, totalFiles, totalBytes, totalBytes);

        // Pin the files to avoid garbage collection.
        using var tx = _connection.BeginTransaction();
        foreach (var hash in pinnedFileHashes)
        {
            _ = new GameBackedUpFile.New(tx)
            {
                GameInstallId = installMetadataId,
                Hash = hash,
            };
        }
        await tx.Commit();
    }

    /// <inheritdoc />
    public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var loadout = RequireLoadout(context);
        var tx = context.Transaction;
        if (context.OverridesGroup is null)
            context.OverridesGroup = context.Synchronizer.GetOrCreateOverridesGroup(tx, loadout);
        var overridesGroupId = LoadoutOverridesGroupId.From(context.OverridesGroup.Value);

        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.IngestFromDisk))
                continue;

            OverridesGroupItems.UpsertFile(tx, loadout, overridesGroupId, path, node.Disk.Hash, node.Disk.Size);
            tx.Add(node.Disk.EntityId, DiskStateEntry.LastModified, new DateTimeOffset(node.Disk.LastModifiedTicks, TimeSpan.Zero));
        }
    }

    /// <inheritdoc />
    public async Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var loadout = RequireLoadout(context);
        var intrinsicFiles = context.Synchronizer.IntrinsicFiles(loadout);
        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.AdaptLoadout)) continue;
            if (node.SourceItemType != LoadoutSourceItemType.Intrinsic) throw new Exception("AdaptLoadout should only be called on intrinsic files");

            var instance = intrinsicFiles[path];
            var resolvedPath = context.Locations.ToAbsolutePath(path);
            await using var stream = resolvedPath.Read();
            await instance.Ingest(stream, loadout, context.SyncTree, context.Transaction);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var tx = context.Transaction;
        HashSet<GamePath> foldersWithDeletedFiles = [];
        var itemIndex = 0;
        
        var deleteFileCount = nodes.Sum(static x => x.Value.Actions.HasFlag(Actions.DeleteFromDisk) ? 1 : 0);
        
        // Delete files from disk
        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.DeleteFromDisk))
                continue;

            context.Job?.ReportProgress(SyncPhases.Deleting, itemIndex, deleteFileCount);
            itemIndex++;

            var resolvedPath = context.Locations.ToAbsolutePath(path);
            resolvedPath.Delete();

            // Only delete the entry if we're not going to replace it
            if (!node.Actions.HasFlag(Actions.ExtractToDisk))
            {
                foldersWithDeletedFiles.Add(path.Parent);

                DiskStateEntries.Retract(tx, context.MetadataId, path, node);
            }
        }

        context.Job?.ReportProgress(SyncPhases.Deleting, deleteFileCount, deleteFileCount);
        return foldersWithDeletedFiles;
    }

    /// <inheritdoc />
    public async Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var tx = context.Transaction;
        List<(Hash Hash, AbsolutePath Path)> toExtract = [];
        
        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.ExtractToDisk))
                continue;
            
            Debug.Assert(node.Loadout.Hash != Hash.Zero, "Loadout hash is zero, this should not happen");

            var resolvedPath = context.Locations.ToAbsolutePath(path);
            toExtract.Add((node.Loadout.Hash, resolvedPath));
        }

        // Extract files to disk
        _logger.LogDebug("Extracting {Count} files to disk", toExtract.Count);
        
        if (toExtract.Count > 0)
        {
            await _fileStore.ExtractFiles(toExtract, CancellationToken.None, UpdateStatus);

            foreach (var (gamePath, node) in nodes)
            {
                if (!node.Actions.HasFlag(Actions.ExtractToDisk))
                    continue;

                var resolvedPath = context.Locations.ToAbsolutePath(gamePath);
                PlannedTimestamps.ApplyTo(context, gamePath, resolvedPath);
                var writeTimeUtc = new DateTimeOffset(resolvedPath.FileInfo.LastWriteTimeUtc);
                
                DeployedFileRows.Record(tx, context, gamePath, node, writeTimeUtc);
                ExecutableExtensions.ApplyTo(resolvedPath);
            }
        }

        void UpdateStatus((int Current, int Max) progress)
        {
            var (current, max) = progress;
            context.Job?.ReportProgress(SyncPhases.Deploying, current, max);
        }
    }

    /// <inheritdoc />
    public Task StampTimestamps(DeploymentContext context, IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)> stamps)
    {
        PlannedTimestamps.Stamp(context, stamps);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var loadout = RequireLoadout(context);
        var intrinsicFiles = context.Synchronizer.IntrinsicFiles(loadout);
        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.WriteIntrinsic)) continue;
            if (node.SourceItemType != LoadoutSourceItemType.Intrinsic) throw new Exception("WriteIntrinsic should only be called on intrinsic files");

            var instance = intrinsicFiles[path];
            // Rendered in memory first: a file that refuses to write must leave the previous one in place,
            // not an empty file (an empty Plugins.txt disables every plugin).
            using var rendered = new MemoryStream();
            await instance.Write(rendered, loadout, context.SyncTree);

            var resolvedPath = context.Locations.ToAbsolutePath(path);
            resolvedPath.Parent.CreateDirectory();

            // Replaced, not truncated: Create opens with O_TRUNC, which would truncate a staged file if the path is
            // still a link from an earlier state (a mod file that became a generated one, a loadout switch).
            // This happens after rendering so a generator can read the previous file; a missing file is fine.
            File.Delete(resolvedPath.ToString());
            await using (var stream = resolvedPath.Create())
            {
                stream.SetLength(0);
                rendered.Position = 0;
                await rendered.CopyToAsync(stream);
            }

            // Recorded like an extracted file. Without a row holding what was just written, the next index sees
            // a file that differs from the previous state, takes it for a change made outside the loadout and
            // ingests it instead of writing, so every other sync would be one behind.
            rendered.Position = 0;
            var hash = await Xx3Hasher.HashAsync(rendered);
            var writeTime = new DateTimeOffset(resolvedPath.FileInfo.LastWriteTimeUtc);
            DeployedFileRows.Record(context.Transaction, context, path, node, hash, Size.From((ulong)rendered.Length), writeTime);
        }
    }

    /// <inheritdoc />
    public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
    {
        var loadout = RequireLoadout(context);
        var tx = context.Transaction;

        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.AddReifiedDelete))
                continue;
            
            if (context.OverridesGroup is null)
                context.OverridesGroup = context.Synchronizer.GetOrCreateOverridesGroup(tx, loadout);

            OverridesGroupItems.AddReifiedDelete(tx, loadout, LoadoutOverridesGroupId.From(context.OverridesGroup.Value), path);
        }
    }

    /// <inheritdoc />
    public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState)
    {
        var processedDirectories = new HashSet<GamePath>();
        var directoriesToDelete = new HashSet<GamePath>();
        var directoriesInUse = new HashSet<GamePath>();
        
        // Build set of directories that are in use (are ancestors of at least one file)
        foreach (var fileEntry in newDiskState)
        {
            var path = (GamePath)fileEntry.Path;
            var parent = path.Parent;
            var rootComponent = parent.GetRootComponent;
        
            // Add all parent directories to the set
            while (parent != rootComponent)
            {
                directoriesInUse.Add(parent);
                parent = parent.Parent;
            }
        }
        
        // Find the highest directory not in use for each deletion 
        foreach (var dirWithDeletion in directoriesWithDeletions)
        {
            var rootComponent = dirWithDeletion.GetRootComponent;
            GamePath? highestEmptyDirectory = null;
            
            var currentParentDir = dirWithDeletion;

            while (currentParentDir != rootComponent)
            {
                if (processedDirectories.Contains(currentParentDir))
                {
                    highestEmptyDirectory = null;
                    break;
                }
                
                // Check if directory contains files or is a parent of directories with files
                if (directoriesInUse.Contains(currentParentDir))
                {
                    break;
                }

                processedDirectories.Add(currentParentDir);
                highestEmptyDirectory = currentParentDir;
                currentParentDir = currentParentDir.Parent;
            }

            if (highestEmptyDirectory != null)
                directoriesToDelete.Add(highestEmptyDirectory.Value);
        }

        foreach (var dir in directoriesToDelete)
        {
            var absolutePath = context.Locations.ToAbsolutePath(dir);
            if (!absolutePath.DirectoryExists()) continue;

            // Filtered files (saves, shader caches) never enter the disk state, so "no tracked file
            // underneath" does not mean "empty". Deleting recursively would take them with it.
            if (absolutePath.EnumerateFiles(recursive: true).Any()) continue;

            // Could have other empty directories as children, so we need to delete recursively
            absolutePath.DeleteDirectory(recursive: true);
        }
    }
}
