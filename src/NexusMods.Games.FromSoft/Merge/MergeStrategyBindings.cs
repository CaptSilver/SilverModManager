using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// One fold step of a merge. A nullable <see cref="ReadOnlySpan{T}"/> does not compile, so a strategy with no vanilla
/// file is told so by <paramref name="hasVanilla"/> and an empty <paramref name="vanilla"/>.
/// </summary>
/// <param name="accumulated">What the fold has produced so far; the higher-priority side.</param>
/// <param name="next">The next, lower-priority file.</param>
/// <param name="baseIsGame">The accumulated side is the installed game's own file, not a mod's merge.</param>
/// <param name="accumulatedName">Who owns the accumulated side, for the notes.</param>
/// <param name="nextName">Who owns the next file, for the notes.</param>
public delegate byte[] MergeFold(
    ReadOnlySpan<byte> accumulated, ReadOnlySpan<byte> next, ReadOnlySpan<byte> vanilla, bool hasVanilla, bool baseIsGame,
    string accumulatedName, string nextName, ICollection<IMergeNote>? notes, FromSoftFormatTraits traits);

/// <summary>
/// The one place a <see cref="MergeStrategy"/> meets the code that performs it. Each merge routine has its own shape;
/// each is adapted to <see cref="MergeFold"/> so the planner folds every strategy the same way.
/// </summary>
public static class MergeStrategyBindings
{
    /// <summary>The fold for a strategy.</summary>
    public static MergeFold For(MergeStrategy strategy) => strategy switch
    {
        MergeStrategy.FmgUnion => (acc, next, _, _, _, accName, nextName, notes, traits)
            => FmgMerge.Union(acc, next, traits, notes, accName, nextName),
        MergeStrategy.FmgThreeWay => (acc, next, vanilla, hasVanilla, _, accName, nextName, notes, traits)
            => FmgMerge.ThreeWay(acc, next, Vanilla(strategy, vanilla, hasVanilla), traits, notes, accName, nextName),
        MergeStrategy.TpfUnion => (acc, next, _, _, _, accName, nextName, notes, traits)
            => TpfMerge.Union(acc, next, traits, notes, accName, nextName),
        MergeStrategy.ParamRows => (acc, next, vanilla, hasVanilla, baseIsGame, accName, nextName, notes, traits)
            => ParamRowMerge.Merge(acc, next, Vanilla(strategy, vanilla, hasVanilla), traits, notes, baseIsGame, accName, nextName),
        // The talk-archive merge does not name its sides: its notes name the entry and state, not a mod.
        MergeStrategy.EsdThreeWay => (acc, next, vanilla, hasVanilla, _, _, _, notes, traits)
            => EsdArchiveMerge.ThreeWay(acc, next, Vanilla(strategy, vanilla, hasVanilla), traits, notes),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
    };

    // The planner refuses a three-way path with no vanilla before folding; this keeps a direct caller from comparing
    // against an empty file.
    private static ReadOnlySpan<byte> Vanilla(MergeStrategy strategy, ReadOnlySpan<byte> vanilla, bool hasVanilla)
        => hasVanilla
            ? vanilla
            : throw new MergeException($"{MergeStrategies.Name(strategy)} needs the vanilla file the mods branched from, and none was given");
}
