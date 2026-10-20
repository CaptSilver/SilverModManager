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
    /// The hashes to keep per installation. An installation whose plan sends nothing to a staging
    /// backend is left out, so sweeping with this result removes its staging directory.
    /// </summary>
    /// <remarks>
    /// A hash is live when a valid loadout of the installation holds it at a path routed to a staging
    /// backend, or when the installation's disk state records it under such a path: a link into the
    /// store would dangle if its target were swept between an apply and the next sync.
    /// </remarks>
    /// <param name="db">The database to read loadouts, disk state and plans from.</param>
    /// <param name="plans">Reads the plan of each installation.</param>
    /// <param name="stagingBackends">The backends that deploy from the staging store.</param>
    public static Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>> LiveHashes(
        IDb db,
        IDeploymentPlanStore plans,
        IReadOnlySet<DeploymentBackendId> stagingBackends)
    {
        var live = new Dictionary<GameInstallMetadataId, HashSet<Hash>>();
        var planByInstallation = new Dictionary<GameInstallMetadataId, DeploymentPlan>();

        foreach (var installation in GameInstallMetadata.All(db))
        {
            var id = installation.GameInstallMetadataId;
            var plan = plans.Load(db, id);
            if (!plan.Roots.Any(r => stagingBackends.Contains(r.Backend))) continue;

            planByInstallation[id] = plan;
            live[id] = [];

            foreach (var entry in DiskStateEntry.FindByGame(db, installation))
            {
                if (stagingBackends.Contains(plan.BackendFor(entry.Path)))
                    live[id].Add(entry.Hash);
            }
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

            var targetPath = loadoutFile.AsLoadoutItemWithTargetPath().TargetPath;
            if (stagingBackends.Contains(plan.BackendFor(targetPath)))
                live[installationId].Add(loadoutFile.Hash);
        }

        return live.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<Hash>)kv.Value);
    }
}
