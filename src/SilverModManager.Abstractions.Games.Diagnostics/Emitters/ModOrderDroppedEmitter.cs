using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>Shows the order entries an import could not place, from the rows the importer stored.</summary>
public sealed class ModOrderDroppedEmitter : ADroppedItemEmitter
{
    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var group in Groups(loadout))
        {
            foreach (var row in group) yield return new DroppedItem(row.Item, row.Source, row.Reason, row.Page);
        }
    }

    /// <summary>
    /// One card per source and page that dropped several entries (a big collection can drop dozens, and a card each buries the
    /// rest of the Health Check), the usual per-item card for one that dropped a single entry. The collection's rule drops
    /// are not order entries: they get their own card and never the re-apply action, because the next import writes them again.
    /// </summary>
    public override async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var group in Groups(loadout))
        {
            var first = group[0];
            var entries = string.Join('\n', group.Select(row => $"- **{row.Item}**: {row.Reason}"));
            var card = group.Count == 1
                ? Card(new DroppedItem(first.Item, first.Source, first.Reason, first.Page))
                : IsRuleDrop(first)
                    ? DroppedItemDiagnostics.CreateDroppedRules(group.Count, first.Source, entries, first.Page)
                    : DroppedItemDiagnostics.CreateDroppedItems(group.Count, first.Source, entries, first.Page);

            // Only a Nexus collection has a manifest to read the order from again, and only its order entries come out of it.
            yield return !IsRuleDrop(first) && NexusCollectionLoadoutGroup.Load(loadout.Db, first.ContainerId.Value).IsValid()
                ? card with { Actions = [DiagnosticActions.ReapplyCollectionOrder(first.ContainerId.Value.Value)] }
                : card;
        }
    }

    private static bool IsRuleDrop(ModOrderDroppedEntry.ReadOnly row) => row.Reason == ModOrderDrop.UnresolvedRuleReason;

    private static List<List<ModOrderDroppedEntry.ReadOnly>> Groups(Loadout.ReadOnly loadout)
    {
        var db = loadout.Db;
        var groups = new Dictionary<(LoadoutItemGroupId Container, string Source, string? Page, bool Rule), List<ModOrderDroppedEntry.ReadOnly>>();
        var order = new List<List<ModOrderDroppedEntry.ReadOnly>>();
        foreach (var row in ModOrderDroppedEntry.FindByLoadout(db, loadout.LoadoutId))
        {
            // The rows are not retracted with their collection, so a row whose container is gone is stale.
            if (!LoadoutItemGroup.Load(db, row.ContainerId).IsValid()) continue;
            var key = (row.ContainerId, row.Source, row.Page, IsRuleDrop(row));
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(list);
            }

            list.Add(row);
        }

        return order;
    }
}
