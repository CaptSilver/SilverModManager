using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.App.GarbageCollection.Nx;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Threading;

namespace NexusMods.App.GarbageCollection.DataModel;

/// <summary>
/// Utility class for running the garbage collector against the DataModel and
/// Nx archives.
/// </summary>
public static class RunGarbageCollector
{
    private static readonly AsyncFriendlyReaderWriterLock _gcLock = new();

    /// <summary/>
    /// <param name="logger"></param>
    /// <param name="archiveLocations">The archive locations, usually obtained from 'DataModelSettings'.</param>
    /// <param name="store">The <see cref="IFileStore"/> that requires locking for concurrent access.</param>
    /// <param name="connection">The MneumonicDB <see cref="IConnection"/> to the DataModel.</param>
    /// <param name="stagingStore">The loose staging store, swept of files no loadout or disk state needs.</param>
    /// <param name="plans">Reads which backend deploys each root of an installation.</param>
    /// <param name="stagingBackends">The backends that deploy from <paramref name="stagingStore"/>.</param>
    public static void Do(
        ILogger logger,
        Span<ConfigurablePath> archiveLocations,
        IFileStore store,
        IConnection connection,
        ILooseStagingStore stagingStore,
        IDeploymentPlanStore plans,
        IReadOnlySet<DeploymentBackendId> stagingBackends)
    {
        // See 'SAFETY' comment below for explanation of 'gcLock'
        using var gcLock = _gcLock.WriteLock();
        var toUpdateInDataStore = new List<ToUpdateInDataStoreEntry>();

        logger.LogInformation("Running garbage collector");
        using (store.WriteLock())
        {
            var gc = new ArchiveGarbageCollector<NxParsedHeaderState, FileEntryWrapper>();
            DataStoreNxArchiveFinder.FindAllArchives(archiveLocations, gc);
            DataStoreReferenceMarker.MarkUsedFiles(connection, gc);
            gc.CollectGarbage(new Progress<double>(), (progress, toArchive, toRemove, archive) =>
            {
                logger.LogInformation("Repacking archive {From} files to {To}", toRemove.Count, toArchive.Count);
                NxRepacker.RepackArchive(progress, toArchive, toRemove, archive, false, out var newArchivePath);
                toUpdateInDataStore.Add(new ToUpdateInDataStoreEntry(toRemove, archive.FilePath, newArchivePath));
            });
            
            // Note(sewer):
            // SAFETY: There is a small risk here that we experience a power outage, or crash when deleting these
            // store paths. In such a case, there may be multiple copies of a file in the store with a given hash.
            // This is not a problem, since the DataStore will only ever use one of them.
            // This is accounted for in `ReloadCaches`.
            foreach (var entry in toUpdateInDataStore)
            {
                // Delete original archive. At this point, all hashes in here have been repacked
                // and are no longer in use.
                entry.OldFilePath.Delete();
            }
        
            store.ReloadCaches();

            // Staged files are copies of file store content, so they are swept under the same lock.
            var live = StagingReferenceMarker.LiveHashes(connection.Db, plans, stagingBackends);
            var swept = stagingStore.Sweep(live);
            if (swept > 0)
                logger.LogInformation("Removed {Count} unused files from the loose staging store", swept);
        }
    }

    private record struct ToUpdateInDataStoreEntry(List<Hash> ToRemove, AbsolutePath OldFilePath, AbsolutePath NewFilePath);
}
