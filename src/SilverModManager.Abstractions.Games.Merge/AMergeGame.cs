using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>A game class implements this to be served by <see cref="MergeEngine"/>.</summary>
public interface IHasMergeGame
{
    AMergeGame MergeGame { get; }
}

/// <summary>What a game tells the engine: which files merge, where vanilla lives, where output goes, how a mod folder ranks.</summary>
public abstract class AMergeGame
{
    /// <summary>Whether the game merges the contested file at <paramref name="contested"/> (the winner's path).</summary>
    public abstract bool Merges(GamePath contested);

    /// <summary>
    /// (Package, Rel) for a path under the mods location: Package is the top mod folder, Rel the path below it, in the
    /// contributor's spelling. Null when the path is not under a mod folder.
    /// </summary>
    public abstract (string Package, string Rel)? Split(GamePath path);

    /// <summary>
    /// Whether two mod folders shipping this path is a conflict at all. False for a path the game loads once per mod
    /// folder with no override between them: it is then neither merged nor reported not mergeable.
    /// </summary>
    public virtual bool Contests(GamePath path) => true;

    /// <summary>Where the game's own copy of <paramref name="rel"/> lives; null when the game ships none.</summary>
    public abstract GamePath? VanillaFor(string rel, GameInstallation installation);

    /// <summary>Where the merged file for <paramref name="rel"/> is written.</summary>
    public abstract GamePath OutputFor(string rel);

    public abstract IMergeStrategy StrategyFor(string rel);

    /// <summary>The bare name of the page where the user reorders mods, e.g. "Load Order". The report says "on the {ReorderHint} page".</summary>
    public abstract string ReorderHint { get; }

    /// <summary>
    /// Higher wins. Defaults to the greatest priority row of <paramref name="owner"/>, or the lowest rank when it has
    /// none. Called once per file while the loadout is listed, from inside the synchronizer's winning-files query, so
    /// an override must not run a database query (it would re-enter the running one) and must not read files.
    /// </summary>
    public virtual ulong Rank(Loadout.ReadOnly loadout, string package, LoadoutItemGroupId owner)
        => LoadoutItemGroupPriority.FindByTarget(loadout.Db, owner)
            .Select(r => r.Priority.Value)
            .DefaultIfEmpty(ConflictPriority.MinValue.Value)
            .Max();
}
