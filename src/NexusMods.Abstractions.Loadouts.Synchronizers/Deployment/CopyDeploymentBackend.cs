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
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Deploys by copying files out of the file store into the game folders, and reads changes back
/// by hashing what is there.
/// </summary>
public sealed class CopyDeploymentBackend : IDeploymentBackend
{
    /// <summary>
    /// We'll limit backups to 5GB, for now we should never see much more than this
    /// of modified game files.
    /// </summary>
    private static Size MaximumBackupSize => Size.GB * 5;

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
        await Parallel.ForEachAsync(nodes, async (item, _) =>
            {
                var (gamePath, node) = item;
                if (!node.Actions.HasFlag(Actions.BackupFile))
                    return;

                var path = installation.Locations.ToAbsolutePath(gamePath);
                Debug.Assert(node.HaveDisk, "Node must have a disk entry to backup");
                
                if (await _fileStore.HaveFile(node.Disk.Hash))
                    return;
                
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
            }
        );

        var totalSize = archivedFiles.Sum(static x => x.Size);
        if (totalSize > MaximumBackupSize)
        {
            var nodeSignatures = nodes
                .Where(static x => x.Value.Actions.HasFlag(Actions.BackupFile))
                .Select(static x => x.Value.Signature)
                .GroupBy(signature => signature)
                .Select(static grp => new {Signature = grp.Key, FileCount = grp.Count()})
                .ToList();
            
            _logger.LogError(
                """
                Cannot backup {FileCount} files with total size {TotalSize}, which exceeds maximum of {MaximumSize}. 
                Node signatures: 
                {Signatures}
                """,
                archivedFiles.Count,
                totalSize,
                MaximumBackupSize,
                string.Join(Environment.NewLine, nodeSignatures.Select(sig => $"  - {sig.Signature}: {sig.FileCount} files"))
            );
            
            throw new Exception($"Cannot backup files, total size is {totalSize}, which is larger than the maximum of {MaximumBackupSize}");
        }
        
        // PERFORMANCE: We deduplicate above with the HaveFile call.
        await _fileStore.BackupFiles(archivedFiles, deduplicate: false);

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
        var overridesGroupId = context.OverridesGroup.Value;
        var newGroup = true;
        LoadoutItemGroup.ReadOnly? overridesGroup = null;
        if (!overridesGroupId.InPartition(PartitionId.Temp))
        {
            newGroup = false;
            overridesGroup = LoadoutItemGroup.Load(loadout.Db, overridesGroupId);
        }

        foreach (var (path, node) in nodes)
        {
            if (!node.Actions.HasFlag(Actions.IngestFromDisk))
                continue;

            // If the overrides group is not new, we need to check if the file is already in the overrides group
            if (!newGroup)
            {
                var existingRecord = overridesGroup!.Value.Children
                    .OfTypeLoadoutItemWithTargetPath()
                    .FirstOrOptional(c => c.TargetPath == path);

                if (existingRecord.HasValue)
                {
                    // Update the disk entry
                    tx.Add(node.Disk.EntityId, DiskStateEntry.LastModified, new DateTimeOffset(node.Disk.LastModifiedTicks, TimeSpan.Zero));
                    
                    // Update the file entry
                    tx.Add(existingRecord.Value.Id, LoadoutFile.Hash, node.Disk.Hash);
                    tx.Add(existingRecord.Value.Id, LoadoutFile.Size, node.Disk.Size);
                    
                    // Skip the rest of this process
                    continue;
                }
            }

            // Entry was added
            var id = tx.TempId();
            var loadoutItem = new LoadoutItem.New(tx, id)
            {
                ParentId = overridesGroupId,
                LoadoutId = loadout.Id,
                Name = path.FileName,
            };
            var loadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                LoadoutItem = loadoutItem,
                TargetPath = path.ToGamePathParentTuple(loadout.Id),
            };

            _ = new LoadoutFile.New(tx, id)
            {
                LoadoutItemWithTargetPath = loadoutItemWithTargetPath,
                Hash = node.Disk.Hash,
                Size = node.Disk.Size,
            };
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

            if (itemIndex % 1000f == 0)
                context.Job?.SetStatus($"({itemIndex}/{deleteFileCount}) Deleting files");
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
                var writeTimeUtc = new DateTimeOffset(resolvedPath.FileInfo.LastWriteTimeUtc);
                
                // Reuse the old disk state entry if it exists
                if (node.HaveDisk)
                {
                    var id = node.Disk.EntityId;
                    tx.Add(id, DiskStateEntry.Hash, node.Loadout.Hash);
                    tx.Add(id, DiskStateEntry.Size, node.Loadout.Size);
                    tx.Add(id, DiskStateEntry.LastModified, writeTimeUtc);
                }
                else
                {
                    _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
                    {
                        Path = gamePath.ToGamePathParentTuple(context.MetadataId),
                        Hash = node.Loadout.Hash,
                        Size = node.Loadout.Size,
                        LastModified = writeTimeUtc,
                        GameId = context.MetadataId,
                    };
                }


                // And mark them as executable if necessary
                var ext = resolvedPath.Extension.ToString().ToLower();
                if (ext is not ("" or ".sh" or ".bin" or ".run" or ".py" or ".pl" or ".php" or ".rb" or ".out"
                    or ".elf")) continue;

                // Note (Sewer): I don't think we'd ever need anything other than just 'user' execute, but you can never
                // be sure. Just in case, I'll throw in group and other to match 'chmod +x' behaviour.
                var currentMode = resolvedPath.GetUnixFileMode();
                resolvedPath.SetUnixFileMode(currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }

        void UpdateStatus((int Current, int Max) progress)
        {
            var (current, max) = progress;
            context.Job?.SetStatus($"({current}/{max}) Extracting files");
        }
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
            var resolvedPath = context.Locations.ToAbsolutePath(path);
            resolvedPath.Parent.CreateDirectory();
            await using var stream = resolvedPath.Create();
            stream.SetLength(0);
            await instance.Write(stream, loadout, context.SyncTree);
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
            var overridesGroup = context.OverridesGroup.Value;
            
            // If this is not a new entity, we may have a matching file in the overrides group already
            if (!overridesGroup.InPartition(PartitionId.Temp))
            {
                var group = LoadoutOverridesGroup.Load(loadout.Db, overridesGroup);
                var foundMatch = group.AsLoadoutItemGroup().Children
                    .OfTypeLoadoutItemWithTargetPath()
                    .TryGetFirst(p => p.TargetPath == path, out var match);

                if (foundMatch)
                {

                    // A delete of a delete does nothing
                    if (match.TryGetAsDeletedFile(out var _))
                        continue;

                    // If we found a match, we need to remove the entity itself
                    tx.Delete(match, recursive: false);
                    continue;
                }
            }
                
            _ = new DeletedFile.New(tx, out var id)
            {
                Reason = "Reified delete",
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = path.ToGamePathParentTuple(loadout.Id),
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = path.FileName,
                        ParentId = overridesGroup,
                        LoadoutId = loadout.Id,
                    },
                },
            };
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
