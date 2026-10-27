using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;

/// <summary>Mods live under <c>Mods/&lt;package&gt;/</c> of the game folder; only <c>.txt</c> files merge.</summary>
internal class TestMergeGame : AMergeGame
{
    public const string Prefix = "Mods/";

    public override bool Merges(GamePath contested) => contested.Path.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public override (string Package, string Rel)? Split(GamePath path)
    {
        if (path.LocationId != LocationId.Game || !path.Path.Path.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = path.Path.Path[Prefix.Length..].Split('/', 2);
        return parts.Length < 2 ? null : (parts[0], parts[1]);
    }

    // A per-mod container the game loads for every mod, so two mods shipping one override nothing.
    public override bool Contests(GamePath path) => !path.Path.Path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);

    public override GamePath? VanillaFor(string rel, GameInstallation installation) => new(LocationId.Game, $"vanilla/{rel}");

    public override GamePath OutputFor(string rel) => new(LocationId.Game, $"Mods/merged/{rel}");

    public override IMergeStrategy StrategyFor(string rel) => TextThreeWayMerge.Instance;

    public override string ReorderHint => "Test Order";

    // The last mod listed wins, as on File Conflicts; HigherWinsGame is the other way round.
    public override bool WinnerIsLower => true;
}
