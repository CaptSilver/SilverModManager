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

        var db = loadout.Db;
        foreach (var row in ModOrderDroppedEntry.FindByLoadout(db, loadout.LoadoutId))
        {
            // The rows are not retracted with their collection, so a row whose container is gone is stale.
            if (!LoadoutItemGroup.Load(db, row.ContainerId).IsValid()) continue;
            yield return new DroppedItem(row.Item, row.Source, row.Reason, row.Page);
        }
    }
}
