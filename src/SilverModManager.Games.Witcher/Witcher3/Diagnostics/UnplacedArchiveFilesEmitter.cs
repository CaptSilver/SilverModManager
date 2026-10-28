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
/// Lists the files of an archive the Witcher 3 rules installed that ended up nowhere in the loadout, apart from
/// documentation. Only a group whose files are exactly what the rules produce for its archive is checked: a
/// collection's saved FOMOD choices, its file lists and a hand-picked Advanced install leave files out on purpose.
/// Files are matched by hash, so a <c>.part.txt</c> kept as an INI tweak counts as placed.
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
            // The installer plans over the paths in ordinal order, and the plan depends on which path comes first.
            var entries = archive.Children.OrderBy(e => e.Path.Path, StringComparer.Ordinal).ToArray();
            var paths = entries.Select(e => e.Path.Path).ToArray();
            var plan = Witcher3InstallPlan.Plan(paths, archive.AsLibraryFile().FileName.ToString());
            if (plan is null) continue;

            var hashOf = entries.ToDictionary(e => e.Path.Path, e => e.AsLibraryFile().Hash, StringComparer.Ordinal);
            var expected = plan.Select(p => (Target: p.Target.ToString(), Hash: hashOf[p.Source])).ToHashSet();
            var actual = item.Children.OfTypeLoadoutItemWithTargetPath()
                .Select(child => child.TryGetAsLoadoutFile(out var file) ? (Target: ((GamePath)child.TargetPath).ToString(), Hash: file.Hash) : default)
                .Where(pair => pair.Target is not null)
                .ToHashSet();
            if (!expected.SetEquals(actual)) continue;

            var placed = actual.Select(a => a.Hash).ToHashSet();
            var skippedCopies = Witcher3InstallPlan.SkippedMenuXmlCopies(paths);
            foreach (var entry in entries)
            {
                if (placed.Contains(entry.AsLibraryFile().Hash)) continue;
                if (skippedCopies.Contains(entry.Path.Path)) continue;
                if (Witcher3ModInstaller.DocumentationExtensions.Contains(entry.Path.Extension)) continue;

                yield return new DroppedItem(entry.Path.Path, item.AsLoadoutItem().Name, Reason, "Library");
            }
        }
    }
}
