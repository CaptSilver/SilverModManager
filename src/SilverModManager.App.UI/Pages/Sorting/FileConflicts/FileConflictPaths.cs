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
    /// <param name="merges">The game's merge index, from <see cref="MergesOf"/>; null for a game that merges nothing.</param>
    public static FileConflictPath[] Load(IConnection connection, LoadoutId loadoutId, LoadoutItemGroupPriorityId priorityId, MergePathIndex? merges)
    {
        var db = connection.Db;
        var thisGroup = LoadoutItemGroupPriority.Load(db, priorityId).TargetId;
        var pins = LoadoutFileWinner.FindByLoadout(db, loadoutId).ToArray();

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
            .OrderBy(x => x.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

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

    /// <summary>
    /// Which package paths of <paramref name="loadout"/> are merge inputs, or null when its game merges nothing.
    /// Whether a path is merged depends only on who provides it, so the vanilla copies the real index also looks up in
    /// the game's archives are left out: a merge's strategy and outcome don't matter here.
    /// </summary>
    public static MergePathIndex? MergesOf(Loadout.ReadOnly loadout, Func<LoadoutMergeSources> sources)
    {
        if (loadout.InstallationInstance.Game is not IFromSoftGame game) return null;
        return MergePathIndex.Build(sources().Providers(loadout), game.Formats, new HashSet<string>());
    }

    private static string NameOf(LoadoutItemGroup.ReadOnly group, LoadoutItemGroupId thisGroup)
    {
        var name = group.AsLoadoutItem().Name;
        return group.LoadoutItemGroupId == thisGroup ? string.Format(Language.FileConflicts_Paths_This, name) : name;
    }
}
