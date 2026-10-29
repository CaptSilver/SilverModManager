using System.Collections.Frozen;
using System.Runtime.CompilerServices;
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
    /// One card per source that dropped several entries (a big collection can drop dozens, and a card each buries the rest of
    /// the Health Check), the usual per-item card for a source that dropped one.
    /// </summary>
    public override async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var group in Groups(loadout))
        {
            if (group.Count == 1)
            {
                yield return Card(new DroppedItem(group[0].Item, group[0].Source, group[0].Reason, group[0].Page));
                continue;
            }

            var entries = string.Join('\n', group.Select(row => $"- **{row.Item}**: {row.Reason}"));
            yield return DroppedItemDiagnostics.CreateDroppedItems(group.Count, group[0].Source, entries, group[0].Page);
        }
    }

    private static List<List<ModOrderDroppedEntry.ReadOnly>> Groups(Loadout.ReadOnly loadout)
    {
        var db = loadout.Db;
        var groups = new Dictionary<(LoadoutItemGroupId Container, string Source), List<ModOrderDroppedEntry.ReadOnly>>();
        var order = new List<List<ModOrderDroppedEntry.ReadOnly>>();
        foreach (var row in ModOrderDroppedEntry.FindByLoadout(db, loadout.LoadoutId))
        {
            // The rows are not retracted with their collection, so a row whose container is gone is stale.
            if (!LoadoutItemGroup.Load(db, row.ContainerId).IsValid()) continue;
            if (!groups.TryGetValue((row.ContainerId, row.Source), out var list))
            {
                groups[(row.ContainerId, row.Source)] = list = [];
                order.Add(list);
            }

            list.Add(row);
        }

        return order;
    }
}
