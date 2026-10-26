using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Resources;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Sorting;

/// <summary>One conflicting path of a mod, as the per-path list shows it.</summary>
/// <param name="Path">The contested path.</param>
/// <param name="WinnerName">The mod whose file is deployed, a pin included.</param>
/// <param name="ThisWins">Whether the mod the list was opened for is the winner.</param>
/// <param name="Losers">The names of the mods whose files lose, the opened mod marked "(this)".</param>
/// <param name="PinnedToName">The mod the path is pinned to; null when priority decides.</param>
/// <param name="PinsApply">False for a path that a pin cannot change.</param>
public sealed record FileConflictPath(GamePath Path, string WinnerName, bool ThisWins, IReadOnlyList<string> Losers, string? PinnedToName, bool PinsApply);

public static class FileConflictPaths
{
    /// <summary>
    /// The paths <paramref name="priorityId"/>'s mod wins or loses, read from the winner queries, so a pin shows
    /// as the winner like any other.
    /// </summary>
    /// <param name="merges">The game's merge index, from <see cref="IMergeIndexSource"/>; null for a game that merges nothing.</param>
    public static FileConflictPath[] Load(IConnection connection, LoadoutId loadoutId, LoadoutItemGroupPriorityId priorityId, MergePathIndex? merges)
    {
        var db = connection.Db;
        var thisGroup = LoadoutItemGroupPriority.Load(db, priorityId).TargetId;
        var pins = LoadoutFileWinner.FindByLoadout(db, loadoutId).ToArray();

        var sharedRows = merges is null ? [] : SharedFileRows(merges, thisGroup);

        return FileConflictsTreeDataGridAdapter.GetPriorityConflicts(connection, priorityId)
            .Select(tuple => (
                Path: (GamePath)LoadoutItemWithTargetPath.Load(db, tuple.WinnerLoadoutItemId).TargetPath,
                Winner: LoadoutItemGroupPriority.Load(db, tuple.WinnerPriorityId).Target,
                Loser: LoadoutItemGroupPriority.Load(db, tuple.LoserPriorityId).Target))
            .GroupBy(x => x.Path)
            .Select(group =>
            {
                var winner = group.First().Winner;
                var pin = pins.FirstOrDefault(p => p.TargetPath.Item2 == group.Key.LocationId && p.TargetPath.Item3 == group.Key.Path);
                var losers = group.Select(x => x.Loser).DistinctBy(x => x.LoadoutItemGroupId)
                    .Select(x => NameOf(x, thisGroup)).ToArray();
                return new FileConflictPath(
                    group.Key,
                    winner.AsLoadoutItem().Name,
                    winner.LoadoutItemGroupId == thisGroup,
                    losers,
                    pin.IsValid() ? LoadoutItemGroup.Load(db, pin.WinnerGroupId).AsLoadoutItem().Name : null,
                    PinsApply(group.Key, merges));
            })
            .Concat(sharedRows)
            .OrderBy(x => x.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Per mod, how many files shared with another me3 package it wins and how many it loses. The sync tree keys a
    /// conflict on the full target path, which differs by package folder, so none of these show up in its counts.
    /// </summary>
    public static Dictionary<LoadoutItemGroupId, (long Winning, long Losing)> SharedFileCounts(MergePathIndex merges)
    {
        var counts = new Dictionary<LoadoutItemGroupId, (long Winning, long Losing)>();
        foreach (var file in SharedPackageFiles.Of(merges))
        {
            if (GroupOf(file.Winner) is { } winner)
                counts[winner] = counts.TryGetValue(winner, out var known) ? (known.Winning + 1, known.Losing) : (1, 0);
            foreach (var loser in file.Losers)
            {
                if (GroupOf(loser) is not { } group) continue;
                counts[group] = counts.TryGetValue(group, out var known) ? (known.Winning, known.Losing + 1) : (0, 1);
            }
        }

        return counts;
    }

    private static IEnumerable<FileConflictPath> SharedFileRows(MergePathIndex merges, LoadoutItemGroupId thisGroup)
    {
        foreach (var file in SharedPackageFiles.Of(merges))
        {
            var own = GroupOf(file.Winner) == thisGroup ? file.Winner : file.Losers.FirstOrDefault(l => GroupOf(l) == thisGroup);
            if (own.PackageId is null) continue;

            string Name(ProviderFile f) => GroupOf(f) == thisGroup ? string.Format(Language.FileConflicts_Paths_This, f.ModName) : f.ModName;
            var thisWins = own.PackageId == file.Winner.PackageId;
            yield return new FileConflictPath(
                new GamePath(LocationId.Me3Mods, $"{own.PackageId}/{file.RelPath}"),
                file.Winner.ModName,
                thisWins,
                file.Losers.Select(Name).ToArray(),
                PinnedToName: null,
                PinsApply: false);
        }
    }

    private static LoadoutItemGroupId? GroupOf(ProviderFile file)
        => file is { HasPriority: true, ModId: { } mod } ? LoadoutItemGroupId.From(mod.Value) : null;

    /// <summary>
    /// Whether a pin can change what is deployed at <paramref name="path"/>. A package file that feeds a merge cannot:
    /// me3 mounts the merged file, which is built from every mod's copy in priority order, and when the copies can't be
    /// merged me3 picks between the packages, which it ranks by priority too. A path only one package provides is not
    /// merged, so the sync tree's winner, pin included, is what deploys.
    /// </summary>
    public static bool PinsApply(GamePath path, MergePathIndex? merges)
    {
        if (merges is null || path.LocationId != LocationId.Me3Mods) return true;

        var parts = path.Path.Path.Split('/', 2);
        if (parts.Length < 2) return true;
        return merges.Lookup(parts[1]) is not { } candidate
               || !candidate.Contributors.Any(c => c.PackageId.Equals(parts[0], StringComparison.Ordinal));
    }

    private static string NameOf(LoadoutItemGroup.ReadOnly group, LoadoutItemGroupId thisGroup)
    {
        var name = group.AsLoadoutItem().Name;
        return group.LoadoutItemGroupId == thisGroup ? string.Format(Language.FileConflicts_Paths_This, name) : name;
    }
}
