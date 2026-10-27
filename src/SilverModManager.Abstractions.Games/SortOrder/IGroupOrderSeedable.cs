using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A sort-order variety whose stored order can be seeded from the order of the mod groups that own its items.
/// </summary>
public interface IGroupOrderSeedable
{
    /// <summary>
    /// Reorders the items that belong to the groups in <paramref name="winnerToLoser"/> so the winning group's items come out
    /// on the winning side (first for SmallerIndexWins, last for GreaterIndexWins), stable within a group, using only the
    /// index slots those items already hold; items of other groups do not move. Writes into <paramref name="tx"/>.
    /// </summary>
    void SeedFromGroupOrder(SortOrderId sortOrderId, IReadOnlyList<LoadoutItemGroupId> winnerToLoser, ITransaction tx, IDb db);
}

public static class GroupOrderSlots
{
    /// <summary>
    /// For each input item, in input order, the index it takes once the items of the listed groups are arranged in
    /// <paramref name="winnerToLoser"/> order across the slots (the indexes) those items held between them.
    /// </summary>
    public static IReadOnlyList<int> SlotOrder(IReadOnlyList<(int Index, LoadoutItemGroupId? Group)> items, IReadOnlyList<LoadoutItemGroupId> winnerToLoser, IndexOverrideBehavior behavior)
    {
        var rank = new Dictionary<LoadoutItemGroupId, int>();
        foreach (var group in winnerToLoser) rank.TryAdd(group, rank.Count);

        var block = Enumerable.Range(0, items.Count)
            .Where(i => items[i].Group is { } group && rank.ContainsKey(group))
            .ToList();

        var slots = block.Select(i => items[i].Index).Order().ToList();

        // Losers come first when the greater index wins; OrderBy is stable, so a group keeps its own order.
        var arranged = block.OrderBy(i => items[i].Index).ToList();
        arranged = behavior == IndexOverrideBehavior.GreaterIndexWins
            ? arranged.OrderByDescending(i => rank[items[i].Group!.Value]).ToList()
            : arranged.OrderBy(i => rank[items[i].Group!.Value]).ToList();

        var result = items.Select(item => item.Index).ToArray();
        for (var slot = 0; slot < arranged.Count; slot++)
            result[arranged[slot]] = slots[slot];
        return result;
    }
}
