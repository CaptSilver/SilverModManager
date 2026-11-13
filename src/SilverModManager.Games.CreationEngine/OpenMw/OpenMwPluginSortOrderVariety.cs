using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.SortOrder;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// The plugin load order of OpenMW. It is the one game whose list has a second key, <c>groundcover=</c>, so it is
/// the only variety that offers the Groundcover switch on a row; the Load Order page shows the switch by asking
/// for <see cref="IGroundcoverSortOrderVariety"/>.
/// </summary>
public sealed class OpenMwPluginSortOrderVariety(IServiceProvider serviceProvider, ICreationEngineGame game)
    : PluginSortOrderVariety(serviceProvider, game), IGroundcoverSortOrderVariety
{
    public Task SetGroundcover(SortOrderId sortOrderId, ISortItemKey item, bool groundcover, CancellationToken token = default) =>
        SetGroundcoverCore(sortOrderId, item, groundcover, token);
}
