using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Controls.Spine;

internal static class LoadoutSpineOrder
{
    /// <summary>
    /// Returns <paramref name="order"/> with the item at <paramref name="fromIndex"/> taken out
    /// and re-inserted so that it ends up at <paramref name="toIndex"/>.
    /// </summary>
    public static IReadOnlyList<LoadoutId> Move(IReadOnlyList<LoadoutId> order, int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromIndex, order.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toIndex, order.Count);

        var result = order.ToList();
        var moved = result[fromIndex];
        result.RemoveAt(fromIndex);
        result.Insert(toIndex, moved);
        return result;
    }
}
