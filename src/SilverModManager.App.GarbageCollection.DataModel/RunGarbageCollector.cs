using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.App.GarbageCollection.Nx;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Threading;

namespace SilverModManager.App.GarbageCollection.DataModel;

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
    /// <param name="storeFor">Finds the staging store a folder's deployed files link from. Without it, a staged file is kept in every store.</param>
    public static void Do(
        ILogger logger,
        Span<ConfigurablePath> archiveLocations,
        IFileStore store,
        IConnection connection,
        ILooseStagingStore stagingStore,
        IDeploymentPlanStore plans,
        IReadOnlySet<DeploymentBackendId> stagingBackends,
        StagingStoreLookup? storeFor = null)
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
        }

        // Outside the archive lock on purpose: an apply holds the staging store and then reads the
        // archives to extract, so sweeping (which waits for that hold) while holding the archive write
        // lock would leave each side waiting for the other. 'gcLock' still keeps runs apart. Liveness is
        // read inside Sweep, after the holds are gone, so it includes what the apply just committed.
        var swept = stagingStore.Sweep(() => StagingReferenceMarker.LiveHashes(connection.Db, plans, stagingBackends, storeFor));
        if (swept > 0)
            logger.LogInformation("Removed {Count} unused files from the loose staging store", swept);
    }

    private record struct ToUpdateInDataStoreEntry(List<Hash> ToRemove, AbsolutePath OldFilePath, AbsolutePath NewFilePath);
}
