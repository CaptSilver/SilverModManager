using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Overrides;

/// <summary>
/// Backs a file up to the store, then trashes or deletes it. The store copy is what makes a delete here recoverable:
/// <see cref="FreedesktopTrash"/> only reaches the home file system, and a Steam library on a second drive is refused.
/// </summary>
internal sealed class GameFolderFileRemoval(IFileStore store, ITrash trash, ILogger<GameFolderFileRemoval> logger) : IGameFolderFileRemoval
{
    public async Task EnsureStored(Loadout.ReadOnly loadout, IReadOnlyList<(GamePath Path, FileIdentity Identity)> files, CancellationToken token)
    {
        var toBackUp = new List<ArchivedFileEntry>();
        foreach (var (path, identity) in files)
        {
            var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
            var stored = await store.HaveFile(identity);

            if (!onDisk.FileExists)
            {
                if (stored) continue;
                throw new GameFolderFileRemovalRefusedException($"`{path.Path}` is no longer in the game folder and the app has no copy of it. Apply first, then remove it.");
            }

            // Hashed even when the store has the identity: the bytes about to go may be newer than the stored ones.
            var actual = FileIdentity.XxHash3(await FileHashing.XxHash3(onDisk, token));
            if (!actual.Equals(identity))
                throw new GameFolderFileRemovalRefusedException($"`{path.Path}` changed on disk since the last sync. Apply first, then remove it.");

            if (!stored)
                toBackUp.Add(new ArchivedFileEntry(new NativeFileStreamFactory(onDisk), identity, onDisk.FileInfo.Size));
        }

        if (toBackUp.Count > 0)
            await store.BackupFiles(toBackUp, deduplicate: false, token);
    }

    public Task<RemovedFromDisk> TakeOffDisk(Loadout.ReadOnly loadout, IReadOnlyList<GamePath> paths, CancellationToken token)
    {
        var trashed = new List<GamePath>();
        var deleted = new List<GamePath>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
            if (!onDisk.FileExists) continue;

            try
            {
                trash.MoveToTrash(onDisk.GetFullPath());
                trashed.Add(path);
            }
            catch (HostFolderMoveRefusedException e)
            {
                logger.LogInformation(e, "The trash refused {Path}; deleting it, its bytes are in the file store", path);
                onDisk.Delete();
                deleted.Add(path);
            }
        }
        return Task.FromResult(new RemovedFromDisk(trashed, deleted));
    }
}
