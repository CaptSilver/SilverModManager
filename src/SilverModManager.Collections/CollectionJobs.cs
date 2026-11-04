using System.Reactive.Linq;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>
/// Which running jobs are working on a collection in a loadout. Updating, reinstalling and the collection pages all ask
/// the same question, so they share this answer: an install of the whole collection (any revision of it) and an
/// install of one of its downloads both count.
/// </summary>
public static class CollectionJobs
{
    /// <summary>
    /// Whether <paramref name="job"/> installs the collection, or one of its downloads, into the loadout.
    /// </summary>
    public static bool TouchesCollection(IJobDefinition job, EntityId collection, LoadoutId loadout) => job switch
    {
        InstallCollectionJob install => install.TargetLoadout == loadout && install.RevisionMetadata.Collection.Id == collection,
        InstallCollectionDownloadJob download => download.TargetLoadout == loadout && download.Item.CollectionRevision.Collection.Id == collection,
        _ => false,
    };

    /// <summary>
    /// Whether a job that has not finished touches the collection in the loadout right now.
    /// </summary>
    public static bool IsBusy(IJobMonitor jobMonitor, EntityId collection, LoadoutId loadout) =>
        jobMonitor.Jobs.Any(job => job.Status is JobStatus.Created or JobStatus.Running or JobStatus.Paused && TouchesCollection(job.Definition, collection, loadout));

    /// <summary>
    /// <see cref="IsBusy"/> as an observable. Starts with false, so a subscriber has a value before the job monitor reports,
    /// and emits when the answer changes.
    /// </summary>
    public static IObservable<bool> ObserveBusy(IJobMonitor jobMonitor, EntityId collection, LoadoutId loadout) =>
        jobMonitor.HasActiveJob<InstallCollectionJob>(job => TouchesCollection(job, collection, loadout)).StartWith(false)
            .CombineLatest(
                jobMonitor.HasActiveJob<InstallCollectionDownloadJob>(job => TouchesCollection(job, collection, loadout)).StartWith(false),
                static (whole, single) => whole || single)
            .DistinctUntilChanged();
}
