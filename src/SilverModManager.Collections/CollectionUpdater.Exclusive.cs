using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

public static partial class CollectionUpdater
{
    /// <summary>
    /// Runs <paramref name="work"/> on the collection while nothing else installs into it, updates it or repairs it. It is reported as an
    /// update meanwhile, so an open page keeps its install actions off.
    /// </summary>
    /// <exception cref="CollectionOrderReapplyRefusedException">The collection is being installed or updated; <paramref name="work"/> did not run.</exception>
    public static async Task RunExclusive(IServiceProvider serviceProvider, NexusCollectionLoadoutGroup.ReadOnly group, Func<Task> work, CancellationToken cancellationToken)
    {
        var key = (group.Collection.Id, group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId);
        lock (UpdatingLock)
        {
            if (Updating.Contains(key)) throw new CollectionOrderReapplyRefusedException("The collection is being updated. Wait for it to finish, then try again.");
        }

        await Gate.WaitAsync(cancellationToken);
        SetUpdating(key, true);
        try
        {
            var current = NexusCollectionLoadoutGroup.Load(serviceProvider.GetRequiredService<IConnection>().Db, group.Id);
            if (!current.IsValid()) throw new CollectionOrderReapplyRefusedException("The collection is no longer installed in this loadout.");
            if (serviceProvider.GetRequiredService<IJobMonitor>().Jobs.Any(job => job.Status is JobStatus.Created or JobStatus.Running or JobStatus.Paused && Blocks(job.Definition, current)))
                throw new CollectionOrderReapplyRefusedException("The collection is still being installed. Wait for it to finish, or cancel it, then try again.");

            await work();
        }
        finally
        {
            SetUpdating(key, false);
            Gate.Release();
        }
    }
}
