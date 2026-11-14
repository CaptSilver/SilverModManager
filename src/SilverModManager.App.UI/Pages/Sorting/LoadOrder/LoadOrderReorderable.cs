using System.ComponentModel;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Input.Reorder;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.App.UI.Pages.Sorting;

/// <summary>
/// Moves load order rows by a number of places. The page draws its rows in <paramref name="direction"/>, the variety
/// stores them in ascending index order, so a place down the screen is one up the stored order on a descending page.
/// A refused move throws; the arrow buttons catch it where they show the page's error, a pad reorder reports it itself.
/// </summary>
public sealed class LoadOrderReorderable(ISortOrderVariety variety, SortOrderId sortOrderId, Func<ListSortDirection> direction) : IReorderable<ISortItemKey>
{
    public int CountFor(ISortItemKey item) => variety.GetSortOrderItems(sortOrderId).Count;

    public int IndexOf(ISortItemKey item)
    {
        var items = variety.GetSortOrderItems(sortOrderId);
        var found = items.FirstOrDefault(row => row.Key.Equals(item));
        if (found is null) throw new InvalidOperationException($"Row {item} is not in the {variety.SortOrderVarietyId} order");

        // The stored index can have gaps; the rank among the rows is what the screen counts.
        var stored = items.Count(row => row.SortIndex < found.SortIndex);
        return direction() == ListSortDirection.Ascending ? stored : items.Count - 1 - stored;
    }

    public Task MoveBy(ISortItemKey item, int delta, CancellationToken token) =>
        variety.MoveItemDelta(sortOrderId, item, direction() == ListSortDirection.Ascending ? delta : -delta, token: token);
}
