using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Witcher.Witcher3.Installers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Diagnostics;

/// <summary>
/// Says that a collection's Script Merger was left out. The installer keeps its group in the loadout, empty, so the
/// collection still counts it installed; an empty group of that archive is the whole record, so nothing is stored.
/// </summary>
public sealed class SkippedScriptMergerEmitter : ADroppedItemEmitter
{
    private const string Reason = "the app merges scripts itself on Apply, so Script Merger is not installed";

    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var (group, archive) in ArchiveGroups.Of(loadout))
        {
            var item = group.AsLoadoutItemGroup();
            if (item.Children.Any() || !ScriptMergerArchive.IsScriptMerger(archive)) continue;

            yield return new DroppedItem(archive.AsLibraryFile().FileName.ToString(), Source(group), Reason, "Merges");
        }
    }

    private static string Source(LibraryLinkedLoadoutItem.ReadOnly group)
    {
        var item = group.AsLoadoutItemGroup().AsLoadoutItem();
        return item.HasParent() ? $"collection {item.Parent.AsLoadoutItem().Name}" : "your loadout";
    }
}
