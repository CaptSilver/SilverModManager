using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A sort order variety whose rows can be marked as groundcover, the plugins OpenMW loads through its separate
/// groundcover handling instead of as ordinary content.
/// </summary>
public interface IGroundcoverSortOrderVariety : ISortOrderVariety
{
    /// <summary>
    /// Marks one item of the sort order as groundcover, or takes the mark off.
    /// </summary>
    Task SetGroundcover(SortOrderId sortOrderId, ISortItemKey item, bool groundcover, CancellationToken token = default);
}
