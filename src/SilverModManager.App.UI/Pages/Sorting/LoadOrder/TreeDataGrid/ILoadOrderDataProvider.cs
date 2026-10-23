using System.ComponentModel;
using DynamicData;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.Sdk.Loadouts;
using R3;

namespace SilverModManager.App.UI.Pages.Sorting;

public interface ILoadOrderDataProvider
{
    IObservable<IChangeSet<CompositeItemModel<ISortItemKey>, ISortItemKey>> ObserveLoadOrder(
        ISortOrderVariety sortOrderVariety,
        LoadoutId loadoutId,
        Observable<ListSortDirection> sortDirectionObservable);
}
