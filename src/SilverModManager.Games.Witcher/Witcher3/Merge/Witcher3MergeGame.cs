using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Merge;

/// <summary>
/// What the Witcher 3 tells the merge engine. A mod's scripts sit in <c>Mods/&lt;folder&gt;/content/scripts</c> and the
/// game's own in <c>content/content0/scripts</c>; the merged result goes in the first folder the game reads,
/// <c>mod0000_MergedFiles</c>, so it overrides every mod's copy.
/// </summary>
public sealed class Witcher3MergeGame(IGame game) : AMergeGame
{
    private const string ScriptsFolder = "content/scripts/";
    private const string VanillaScripts = "content/content0/";

    private IWitcherModSortOrderVariety Variety =>
        game.SortOrderManager.GetSortOrderVarieties().OfType<IWitcherModSortOrderVariety>().Single();

    // The merged folder is the app's own output. A copy a Vortex-era merge left in External Changes must not count as a
    // mod, or it would show as a contributor and feed its stale merge back in.
    public override (string Package, string Rel)? Split(GamePath path)
    {
        if (WitcherModFolders.FolderOf(path) is not { } folder) return null;
        if (folder.Equals(WitcherConstants.W3MergedFolder, StringComparison.OrdinalIgnoreCase)) return null;

        var rel = path.Path.Path[(WitcherConstants.W3ModsFolder.Length + 1 + folder.Length + 1)..];
        return (folder, rel);
    }

    public override bool Merges(GamePath contested)
        => Split(contested) is { } split
           && split.Rel.StartsWith(ScriptsFolder, StringComparison.OrdinalIgnoreCase)
           && split.Rel.EndsWith(".ws", StringComparison.OrdinalIgnoreCase);

    // Each mod folder is the game's own namespace: its bundles, caches, string tables and metadata sit beside every
    // other folder's and override nothing. A script path is the one thing the game resolves across folders (it
    // compiles one copy), so it is merged. A loose xml is kept Contested (reported, not merged) until we know the game
    // never loads one from a mod's content folder.
    public override SharedPath Sharing(GamePath path)
        => Merges(path) || (Split(path) is { } split && split.Rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            ? SharedPath.Contested
            : SharedPath.Independent;

    // A mod's rel starts with content/, the game's copy sits below content/content0/ instead. Always a path: the engine
    // asks the vanilla trust about it and notes a copy that is missing or was modified. Only a rel outside content/ has
    // no game copy to point at.
    public override GamePath? VanillaFor(string rel, GameInstallation installation)
        => rel.StartsWith("content/", StringComparison.OrdinalIgnoreCase)
            ? new GamePath(LocationId.Game, VanillaScripts + rel["content/".Length..])
            : null;

    public override GamePath OutputFor(string rel)
        => new(LocationId.Game, $"{WitcherConstants.W3ModsFolder}/{WitcherConstants.W3MergedFolder}/{rel}");

    public override IMergeStrategy StrategyFor(string rel) => TextThreeWayMerge.Instance;

    public override string ReorderHint => "Load Order";

    // A folder switched off on the Load Order page is not loaded, so its edits must not live on in the merged copy.
    public override bool Contributes(Loadout.ReadOnly loadout, string package) => Variety.IsSwitchedOn(loadout, package);

    // Two groups can share one folder, and the Load Order page ranks the folder once, so the merge ranks it the same
    // way instead of by the owning group's row.
    public override ulong Rank(Loadout.ReadOnly loadout, string package, LoadoutItemGroupId owner)
        => (Variety.RankOf(loadout, package) ?? ConflictPriority.MinValue).Value;
}
