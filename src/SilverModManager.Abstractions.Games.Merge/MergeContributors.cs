using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>One enabled file under a mod folder. <paramref name="Rel"/> is the path below the folder in this file's own spelling; <paramref name="Prioritised"/> is whether the owning mod has a priority row.</summary>
public readonly record struct ContestedFile(string Package, string Rel, GamePath Target, Hash Hash, ulong Rank, string ModName, LoadoutItemGroupId Owner, bool Prioritised);

/// <summary>
/// A path that more than one mod folder ships. <paramref name="Contributors"/> are ranked highest first, then by
/// package name; <paramref name="Rel"/> is the first one's spelling. <paramref name="Strategy"/> is null when the game
/// does not merge the path.
/// </summary>
public sealed record ContestedPath(string Rel, IReadOnlyList<ContestedFile> Contributors, IMergeStrategy? Strategy);

/// <summary>Finds the paths several mod folders ship, from the loadout's rows alone.</summary>
public static class MergeContributors
{
    /// <summary>
    /// Every enabled file under a mod folder that the game contests, grouped by Rel case-insensitively, only groups
    /// provided by more than one mod. A mod counts once per path, and when several mods ship the very same target only
    /// the one the synchronizer deploys counts. Sorted by Rel ordinal. Reads rows through the model accessors and no file
    /// bytes, so the synchronizer can call it while its winning-files query runs: a database query there would re-enter
    /// the running one.
    /// </summary>
    public static IReadOnlyList<ContestedPath> Of(Loadout.ReadOnly loadout, AMergeGame game)
        => Group(loadout, game, contested: true)
            .Select(g => new ContestedPath(g[0].Rel, g, game.Merges(g[0].Target) ? game.StrategyFor(g[0].Rel) : null))
            .ToArray();

    /// <summary>
    /// The paths several mod folders ship that the game says are no contest (it loads each mod's copy), in the same
    /// order and shape as <see cref="Of"/> with no strategy. They are not merged and not reported not mergeable, but a
    /// caller can still tell the user that the lower mod's copy of such a path is not used.
    /// </summary>
    public static IReadOnlyList<ContestedPath> NotContested(Loadout.ReadOnly loadout, AMergeGame game)
        => Group(loadout, game, contested: false).Select(g => new ContestedPath(g[0].Rel, g, null)).ToArray();

    private static List<ContestedFile[]> Group(Loadout.ReadOnly loadout, AMergeGame game, bool contested)
    {
        var db = loadout.Db;
        var priorityRows = new Dictionary<LoadoutItemGroupId, bool>();
        var files = new List<ContestedFile>();

        foreach (var item in LoadoutItem.FindByLoadout(db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var chain = item.AsLoadoutItem().GetThisAndParents().ToArray();
            if (chain.Any(p => p.IsDisabled)) continue;

            var target = (GamePath)item.TargetPath;
            if (game.Split(target) is not var (package, rel)) continue;
            if (game.Contests(target) != contested) continue;

            // Only a file has bytes to merge; anything else with a target path is a marker.
            if (!item.TryGetAsLoadoutFile(out var file)) continue;

            // The installer puts the priority on the mod's group, and a collection's rows are remapped onto the mods
            // inside it, so the first group above the file that has a row is the mod, collection or not.
            LoadoutItem.ReadOnly? mod = null;
            foreach (var ancestor in chain.Skip(1))
            {
                var id = LoadoutItemGroupId.From(ancestor.LoadoutItemId.Value);
                if (!priorityRows.TryGetValue(id, out var has))
                    priorityRows[id] = has = LoadoutItemGroupPriority.FindByTarget(db, id).Any();
                if (!has) continue;

                mod = ancestor;
                break;
            }

            var owner = mod ?? (chain.Length > 1 ? chain[1] : chain[0]);
            var ownerId = LoadoutItemGroupId.From(owner.LoadoutItemId.Value);
            files.Add(new ContestedFile(package, rel, target, file.Hash, game.Rank(loadout, package, ownerId), owner.Name, ownerId, mod is not null));
        }

        return Collapse(files)
            .GroupBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderByDescending(f => f.Rank).ThenBy(f => f.Package, StringComparer.Ordinal).ThenBy(f => f.ModName, StringComparer.Ordinal).ToArray())
            .OrderBy(g => g[0].Rel, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// One file per target, the highest-ranked mod's, which is the one the synchronizer deploys (the winner of a path is
    /// the mod with the greatest priority); then one file per mod and relative path, so a mod that ships the same path
    /// under two of its package folders does not merge with itself. Ties go to the lower package name, then mod name.
    /// </summary>
    private static IEnumerable<ContestedFile> Collapse(List<ContestedFile> files)
    {
        var order = files.OrderByDescending(f => f.Rank).ThenBy(f => f.Package, StringComparer.Ordinal).ThenBy(f => f.ModName, StringComparer.Ordinal).ToArray();
        var targets = new HashSet<GamePath>();
        var perMod = new HashSet<(LoadoutItemGroupId, string)>();
        foreach (var f in order)
        {
            if (!targets.Add(f.Target)) continue;
            if (!perMod.Add((f.Owner, f.Rel.ToUpperInvariant()))) continue;
            yield return f;
        }
    }
}
