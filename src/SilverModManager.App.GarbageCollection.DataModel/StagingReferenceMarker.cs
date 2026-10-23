using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.GarbageCollection.DataModel;

/// <summary>
/// Works out which files in the loose staging store are still needed.
/// </summary>
public static class StagingReferenceMarker
{
    /// <summary>
    /// The hashes to keep, per store and installation. An installation whose plan sends nothing to a staging
    /// backend is left out, so sweeping with this result removes its staging directory.
    /// </summary>
    /// <remarks>
    /// A hash is live when a valid loadout of the installation holds it at a path routed to a staging
    /// backend, or when the installation's disk state records it under such a path: a link into the
    /// store would dangle if its target were swept between an apply and the next sync. It is live in the
    /// store that path's folder links from, which is not the one a game used before it was moved to
    /// another filesystem. When <paramref name="storeFor"/> is null, or can't tell, it is live in every store.
    /// </remarks>
    /// <param name="db">The database to read loadouts, disk state and plans from.</param>
    /// <param name="plans">Reads the plan of each installation.</param>
    /// <param name="stagingBackends">The backends that deploy from the staging store.</param>
    /// <param name="storeFor">Finds the store a folder's deployed files link from.</param>
    public static StagingLiveness LiveHashes(
        IDb db,
        IDeploymentPlanStore plans,
        IReadOnlySet<DeploymentBackendId> stagingBackends,
        StagingStoreLookup? storeFor = null)
    {
        var live = new StagingLiveness();
        var planByInstallation = new Dictionary<GameInstallMetadataId, DeploymentPlan>();

        foreach (var installation in GameInstallMetadata.All(db))
        {
            var plan = plans.Load(db, installation.GameInstallMetadataId);
            if (plan.Roots.Any(r => stagingBackends.Contains(r.Backend))) planByInstallation[installation.GameInstallMetadataId] = plan;
        }

        // One lookup per folder: it stats the path to find its filesystem.
        var storeByFolder = new Dictionary<(GameInstallMetadataId, DeploymentBackendId, GamePath), ILooseStagingStore?>();
        void MarkLive(GameInstallMetadataId installation, DeploymentPlan plan, GamePath path, Hash hash)
        {
            var backend = plan.BackendFor(path);
            if (!stagingBackends.Contains(backend)) return;

            var key = (installation, backend, path.Parent);
            if (!storeByFolder.TryGetValue(key, out var store))
                storeByFolder[key] = store = storeFor?.Invoke(installation, backend, path.Parent);

            if (store is null) live.AddEverywhere(installation, hash);
            else live.Add(store.Root, installation, hash);
        }

        foreach (var installation in GameInstallMetadata.All(db))
        {
            var id = installation.GameInstallMetadataId;
            if (!planByInstallation.TryGetValue(id, out var plan)) continue;

            foreach (var entry in DiskStateEntry.FindByGame(db, installation))
                MarkLive(id, plan, entry.Path, entry.Hash);
        }

        var isLoadoutValid = new Dictionary<LoadoutId, bool>();
        foreach (var loadoutFile in LoadoutFile.All(db))
        {
            var item = new LoadoutItem.ReadOnly(db, loadoutFile.Id);
            if (!isLoadoutValid.TryGetValue(item.LoadoutId, out var valid))
            {
                valid = item.Loadout.IsValid();
                isLoadoutValid[item.LoadoutId] = valid;
            }
            if (!valid) continue;

            var installationId = item.Loadout.InstallationId;
            if (!planByInstallation.TryGetValue(installationId, out var plan)) continue;

            MarkLive(installationId, plan, loadoutFile.AsLoadoutItemWithTargetPath().TargetPath, loadoutFile.Hash);
        }

        return live;
    }
}
