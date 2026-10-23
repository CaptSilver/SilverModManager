using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using DynamicData.Kernel;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Deployment;

public sealed class DeploymentPlanStore : IDeploymentPlanStore
{
    // Entries added to a transaction are invisible to the db it was opened from, so a Write after an
    // EnsureComplete in the same transaction would add a second entity for the root. The transaction's own
    // additions are remembered here and edited in place instead.
    private readonly ConditionalWeakTable<ITransaction, List<PendingEntry>> _pending = new();

    private sealed record PendingEntry(GameInstallMetadataId Installation, GamePath Root, DeploymentPlanEntry.New Entry);

    public DeploymentPlan Load(IDb db, GameInstallMetadataId installation)
    {
        var roots = ImmutableArray.CreateBuilder<DeploymentPlanRoot>();
        foreach (var entry in DeploymentPlanEntry.FindByInstallation(db, installation))
        {
            roots.Add(new DeploymentPlanRoot(
                entry.Root,
                new DeploymentBackendId(entry.Backend),
                entry.Chosen is { HasValue: true, Value: true },
                entry.Reason.HasValue ? entry.Reason.Value : null));
        }
        return new DeploymentPlan(roots.ToImmutable());
    }

    public DeploymentPlan EnsureComplete(ITransaction tx, IDb db, GameInstallation installation, GameInstallMetadataId id, IDeploymentBackendSelector selector)
    {
        var plan = Load(db, id);
        var roots = plan.Roots.ToBuilder();
        foreach (var missing in plan.MissingRoots(installation.Locations))
        {
            var choice = selector.Choose(installation, missing);
            AddEntry(tx, id, missing, choice.Backend, chosen: false, choice.Reason);
            roots.Add(new DeploymentPlanRoot(missing, choice.Backend, Chosen: false, choice.Reason));
        }
        return new DeploymentPlan(roots.ToImmutable());
    }

    public void Write(ITransaction tx, IDb db, GameInstallMetadataId installation, GamePath root, DeploymentBackendId backend, bool chosen)
    {
        var pending = _pending.GetValue(tx, _ => []);
        var added = pending.FirstOrDefault(p => p.Installation == installation && p.Root == root);
        if (added is not null)
        {
            added.Entry.Backend = backend.Value;
            added.Entry.Chosen = chosen ? true : null;
            added.Entry.Reason = null;
            return;
        }

        var existing = DeploymentPlanEntry.FindByInstallation(db, installation).FirstOrDefault(e => e.Root == root);
        if (!existing.IsValid())
        {
            AddEntry(tx, installation, root, backend, chosen, reason: null);
            return;
        }

        tx.Add(existing.Id, DeploymentPlanEntry.Backend, backend.Value);
        // A reason describes the pick it was recorded with, so it goes when the backend changes.
        if (existing.Reason.HasValue) tx.Retract(existing.Id, DeploymentPlanEntry.Reason, existing.Reason.Value);
        if (chosen) tx.Add(existing.Id, DeploymentPlanEntry.Chosen, true);
        else if (existing.Chosen.HasValue) tx.Retract(existing.Id, DeploymentPlanEntry.Chosen, existing.Chosen.Value);
    }

    private void AddEntry(ITransaction tx, GameInstallMetadataId installation, GamePath root, DeploymentBackendId backend, bool chosen, string? reason)
    {
        var entry = new DeploymentPlanEntry.New(tx)
        {
            InstallationId = installation,
            Root = root.ToGamePathParentTuple(installation),
            Backend = backend.Value,
        };
        if (chosen) entry.Chosen = true;
        if (reason is not null) entry.Reason = reason;
        _pending.GetValue(tx, _ => []).Add(new PendingEntry(installation, root, entry));
    }
}
