using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Witcher.Witcher3.Installers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Diagnostics;

/// <summary>
/// Lists the files of an installed archive that ended up nowhere in the loadout, apart from documentation. Found by
/// matching the archive's file hashes against the group's files, so a <c>.part.txt</c> kept as an INI tweak counts as placed.
/// </summary>
public sealed class UnplacedArchiveFilesEmitter : ADroppedItemEmitter
{
    private const string Reason = "no install rule for The Witcher 3 places it";

    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var (group, archive) in ArchiveGroups.Of(loadout))
        {
            // The skipped Script Merger has its own card.
            if (ScriptMergerArchive.IsScriptMerger(archive)) continue;

            var item = group.AsLoadoutItemGroup();
            var placed = item.Children.OfTypeLoadoutItemWithTargetPath()
                .Select(child => child.TryGetAsLoadoutFile(out var file) ? file.Hash : default)
                .ToHashSet();

            foreach (var entry in archive.Children.OrderBy(child => child.Path.Path, StringComparer.Ordinal))
            {
                if (placed.Contains(entry.AsLibraryFile().Hash)) continue;
                if (Witcher3ModInstaller.DocumentationExtensions.Contains(entry.Path.Extension)) continue;

                yield return new DroppedItem(entry.Path.Path, item.AsLoadoutItem().Name, Reason, "Library");
            }
        }
    }
}
