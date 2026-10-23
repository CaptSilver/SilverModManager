using NexusMods.Abstractions.Loadouts;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// A sort order variety whose items can be switched on and off from the Load Order page, as plugins can.
/// </summary>
public interface IToggleableSortOrderVariety : ISortOrderVariety
{
    /// <summary>
    /// Enables or disables one item of the sort order.
    /// </summary>
    Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default);
}
