using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.Witcher.Witcher3.Menu;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
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
    private const string LoadOrderPage = "Load Order";
    private const string FileConflictsPage = "File Conflicts";

    /// <param name="Rank">The synchronizer's rank for the copy, which puts a pinned copy above every priority.</param>
    /// <param name="InModsFolder">Whether the same group also has a file under Mods/&lt;folder&gt;, which is what puts it on the Load Order page.</param>
    private readonly record struct Provider(string Name, string Group, UInt128 Rank, bool InModsFolder);

    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var copies in Providers(loadout).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = copies.Key;
            if (MenuXmlPaths.IsNative(name))
            {
                foreach (var copy in copies.OrderBy(c => c.Group, StringComparer.Ordinal))
                    yield return new DroppedItem($"the game's copy of {name}.xml", "the game folder",
                        $"{copy.Group} ships a whole {name}.xml, so the game's own entries in it are replaced", "Library");
            }

            // The synchronizer deploys the highest rank, so the winner is read the same way; equal ranks fall back to the name.
            var ranked = copies.OrderByDescending(c => c.Rank).ThenBy(c => c.Group, StringComparer.Ordinal).ToList();
            var winner = ranked[0];
            var pinned = winner.Rank > ulong.MaxValue;
            foreach (var loser in ranked.Skip(1).OrderBy(c => c.Group, StringComparer.Ordinal))
            {
                var onLoadOrder = !pinned && (winner.InModsFolder || loser.InModsFolder);
                yield return new DroppedItem($"{name}.xml", loser.Group,
                    pinned
                        ? $"you chose {winner.Group}'s copy in File Conflicts, so it is used"
                        : $"{winner.Group} loads after it (lower in the Load Order list), so its copy is used",
                    onLoadOrder ? LoadOrderPage : FileConflictsPage);
            }
        }
    }

    // Ranked the way the synchronizer picks the file: a pin beats every priority, and only the group directly above
    // the file counts.
    private static IEnumerable<Provider> Providers(Loadout.ReadOnly loadout)
    {
        var files = LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Select(file => file.AsLoadoutItemWithTargetPath())
            .Where(file => file.AsLoadoutItem().IsEnabled())
            .ToList();
        var inModsFolder = files
            .Where(file => WitcherModFolders.FolderOf(file.TargetPath) is not null)
            .Select(file => file.AsLoadoutItem().ParentId)
            .ToHashSet();

        var winners = FileWinners.Of(loadout.Db, loadout.LoadoutId);
        foreach (var file in files)
        {
            var path = (GamePath)file.TargetPath;
            if (MenuXmlPaths.NameOf(path) is not { } name) continue;

            var item = file.AsLoadoutItem();
            if (!item.Parent.IsValid()) continue;

            var groupId = LoadoutItemGroupId.From(item.ParentId.Value);
            yield return new Provider(name, item.Parent.AsLoadoutItem().Name, winners.RankOf(path, groupId), inModsFolder.Contains(item.ParentId));
        }
    }
}
