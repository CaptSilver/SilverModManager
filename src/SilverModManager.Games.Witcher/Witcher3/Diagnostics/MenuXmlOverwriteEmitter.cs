using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.Witcher.Witcher3.Menu;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Diagnostics;

/// <summary>
/// The menu config folder holds one file per name, so a mod's XML replaces whatever else sits at its path: the game's
/// own menu file, or another mod's of the same name. The loser is read from the loadout's files because the sync
/// tree keeps only the winner of each path.
/// </summary>
public sealed class MenuXmlOverwriteEmitter : ADroppedItemEmitter
{
    private readonly record struct Provider(string Name, string Group, ConflictPriority Priority);

    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var copies in Providers(loadout).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = copies.Key;
            if (MenuXmlPaths.IsNative(name))
            {
                foreach (var copy in copies.OrderBy(c => c.Group, StringComparer.Ordinal))
                    yield return new DroppedItem($"the game's {name}.xml", copy.Group,
                        $"{copy.Group} ships a whole {name}.xml, so the game's own entries are replaced; Vortex merges these per setting (mergers.ts doMergeXML), this app does not yet", "Library");
            }

            // The winner is the group loaded last, the one lowest on the Load Order page.
            var ranked = copies.OrderByDescending(c => c.Priority.Value).ThenBy(c => c.Group, StringComparer.Ordinal).ToList();
            foreach (var loser in ranked.Skip(1).OrderBy(c => c.Group, StringComparer.Ordinal))
                yield return new DroppedItem($"{loser.Group}'s {name}.xml", loser.Group,
                    $"{ranked[0].Group} loads after it (lower in the Load Order list), so its copy is used", "Load Order");
        }
    }

    // The group that provides a file is the nearest one above it with a priority row, the mod the file belongs to.
    private static IEnumerable<Provider> Providers(Loadout.ReadOnly loadout)
    {
        var priorities = new Dictionary<EntityId, ConflictPriority>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
            priorities[row.TargetId] = row.Priority;

        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            var item = file.AsLoadoutItemWithTargetPath().AsLoadoutItem();
            if (!item.IsEnabled() || MenuXmlPaths.NameOf(file.AsLoadoutItemWithTargetPath().TargetPath) is not { } name) continue;

            var owner = item.GetThisAndParents().Skip(1).FirstOrDefault(ancestor => priorities.ContainsKey(ancestor.Id));
            if (!owner.IsValid()) continue;

            yield return new Provider(name, owner.Name, priorities[owner.Id]);
        }
    }
}
