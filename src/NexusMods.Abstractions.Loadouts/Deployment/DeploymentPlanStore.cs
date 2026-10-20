using System.Collections.Immutable;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Deployment;

public sealed class DeploymentPlanStore(IConnection connection) : IDeploymentPlanStore
{
    public DeploymentPlan Load(IDb db, GameInstallMetadataId installation)
    {
        var roots = ImmutableArray.CreateBuilder<DeploymentPlanRoot>();
        foreach (var entry in DeploymentPlanEntry.FindByInstallation(db, installation))
        {
            roots.Add(new DeploymentPlanRoot(entry.Root, new DeploymentBackendId(entry.Backend)));
        }
        return new DeploymentPlan(roots.ToImmutable());
    }

    public DeploymentPlan EnsureComplete(ITransaction tx, IDb db, GameInstallation installation, GameInstallMetadataId id)
    {
        var plan = Load(db, id);
        var roots = plan.Roots.ToBuilder();
        foreach (var missing in plan.MissingRoots(installation.Locations))
        {
            AddEntry(tx, id, missing, DeploymentBackendId.Copy);
            roots.Add(new DeploymentPlanRoot(missing, DeploymentBackendId.Copy));
        }
        return new DeploymentPlan(roots.ToImmutable());
    }

    // Reads the committed db for the existing entity: the Write signature carries no db, and a root
    // is only ever written once per transaction.
    public void Write(ITransaction tx, GameInstallMetadataId installation, GamePath root, DeploymentBackendId backend)
    {
        var existing = DeploymentPlanEntry.FindByInstallation(connection.Db, installation)
            .FirstOrDefault(e => e.Root == root);
        if (existing.IsValid())
        {
            tx.Add(existing.Id, DeploymentPlanEntry.Backend, backend.Value);
            return;
        }
        AddEntry(tx, installation, root, backend);
    }

    private static void AddEntry(ITransaction tx, GameInstallMetadataId installation, GamePath root, DeploymentBackendId backend)
    {
        _ = new DeploymentPlanEntry.New(tx)
        {
            InstallationId = installation,
            Root = root.ToGamePathParentTuple(installation),
            Backend = backend.Value,
        };
    }
}
