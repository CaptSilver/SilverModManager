namespace NexusMods.Games.Bethesda.Loot.Sorting;

/// <summary>
/// Places where this port knowingly does less than libloot, so a caller can tell the user that
/// the order may differ from LOOT's.
/// </summary>
public readonly record struct KnownDivergences(bool OverlapEdgesSkipped, int UnevaluatedConditions);

/// <summary>A sorted load order and what was left out of the sort.</summary>
public sealed record SortResult(IReadOnlyList<string> Order, KnownDivergences Divergences);

/// <summary>The optional steps of sorting a partition, so that a sort can be run without some of them.</summary>
[Flags]
internal enum SortStages
{
    None = 0,
    Groups = 1,
    Overlap = 2,
    TieBreak = 4,
    All = Groups | Overlap | TieBreak,
}
