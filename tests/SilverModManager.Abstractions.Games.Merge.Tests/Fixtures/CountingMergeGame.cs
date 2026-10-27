using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;

/// <summary>
/// Counts how often the engine asks it things. <see cref="Merges"/> is asked once per contested path each time the
/// loadout is scanned for contests, so its count is the number of planning passes times the number of paths.
/// </summary>
internal sealed class CountingMergeGame : TestMergeGame
{
    public int MergesCalls;
    public int RankCalls;

    public IMergeStrategy Strategy { get; init; } = TextThreeWayMerge.Instance;

    public override IMergeStrategy StrategyFor(string rel) => Strategy;

    public override bool Merges(GamePath contested)
    {
        Interlocked.Increment(ref MergesCalls);
        return base.Merges(contested);
    }

    public override ulong Rank(Loadout.ReadOnly loadout, string package, LoadoutItemGroupId owner)
    {
        Interlocked.Increment(ref RankCalls);
        return base.Rank(loadout, package, owner);
    }
}
