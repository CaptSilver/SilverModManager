namespace SilverModManager.Games.Bethesda.Loot.Sorting;

/// <summary>
/// Places where this port knowingly does less than libloot, so a caller can tell the user that
/// the order may differ from LOOT's.
/// </summary>
public readonly record struct KnownDivergences(bool OverlapEdgesSkipped, int UnevaluatedConditions);

/// <summary>An edge of the final plugin graph: <paramref name="From"/> loads before <paramref name="To"/>.</summary>
public readonly record struct SortEdge(string From, string To, EdgeType Type);

/// <summary>A sorted load order and what was left out of the sort.</summary>
/// <param name="Order">The plugin names in load order.</param>
/// <param name="Divergences">What the caller knows was skipped; the sorter leaves it for the caller to fill in.</param>
/// <param name="OverlapEdgesAdded">Whether any plugin pair got an overlap edge. False for every partition means the
/// plugins reported nothing to overlap on, which is how a caller tells overlap data was missing.</param>
public sealed record SortResult(IReadOnlyList<string> Order, KnownDivergences Divergences, bool OverlapEdgesAdded)
{
    /// <summary>
    /// Pairs of neighbours in the order with no edge between them, one per partition at most. libloot
    /// logs these; they should never occur, because the tie-break edges join every neighbour.
    /// </summary>
    public IReadOnlyList<(string First, string Second)> UnlinkedNeighbours { get; init; } = [];

    /// <summary>
    /// Every edge the sort ended up with, for working out why two plugins are where they are. Empty
    /// unless the sort was asked to record them.
    /// </summary>
    public IReadOnlyList<SortEdge> Edges { get; init; } = [];
}
