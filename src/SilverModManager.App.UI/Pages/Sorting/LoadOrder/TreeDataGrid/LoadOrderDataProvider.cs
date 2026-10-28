using System.ComponentModel;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Sdk.Resources;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Resources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using R3;
using ReactiveUI;
// ReSharper disable InvokeAsExtensionMethod

namespace SilverModManager.App.UI.Pages.Sorting;

public class LoadOrderDataProvider : ILoadOrderDataProvider
{
    private readonly Lazy<IResourceLoader<EntityId, Bitmap>> _thumbnailLoader;
    private readonly IConnection _connection;

    public LoadOrderDataProvider(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _thumbnailLoader = new Lazy<IResourceLoader<EntityId, Bitmap>>(() => ImagePipelines.GetModPageThumbnailPipeline(serviceProvider));
    }
    
    // TODO: support CollectionGroupId for observing load orders for collection groups
    public IObservable<IChangeSet<CompositeItemModel<ISortItemKey>, ISortItemKey>> ObserveLoadOrder(
        ISortOrderVariety sortOrderVariety,
        LoadoutId loadoutId,
        Observable<ListSortDirection> sortDirectionObservable)
    {
        var sortOrderId = sortOrderVariety.GetSortOrderIdFor(loadoutId);
        if (!sortOrderId.HasValue)
            throw new InvalidOperationException($"No sort order found for loadout {loadoutId} and variety {sortOrderVariety.SortOrderVarietyId}");
        
        var refCountedItemsChangeSet = sortOrderVariety.GetSortOrderItemsChangeSet(sortOrderId.Value)
            .RefCount();
        
        var lastIndexObservable = refCountedItemsChangeSet
            .QueryWhenChanged(query => GetLastIndex(query.Items.Select(item => item.SortIndex)))
            .ToObservable()
            .Replay(1)
            .RefCount();
        
        var topMostIndexObservable = R3.Observable.CombineLatest(
                sortDirectionObservable,
                lastIndexObservable,
                (sortDirection, lastIndex) => sortDirection == ListSortDirection.Ascending ? 0 : lastIndex
            )
            .Replay(1)
            .RefCount();

        var bottomMostIndexObservable = R3.Observable.CombineLatest(
                sortDirectionObservable,
                lastIndexObservable,
                (sortDirection, lastIndex) => sortDirection == ListSortDirection.Ascending ? lastIndex : 0
            )
            .Replay(1)
            .RefCount();

        // Scanning the order for its last index would run on the caller's thread, the UI thread when the page opens.
        // The first change set already carries every row, so the index is read from it and the query is subscribed on
        // the task pool.
        return System.Reactive.Linq.Observable.Defer(() =>
            {
                var initialLastIndex = 0;
                var seeded = false;
                return refCountedItemsChangeSet
                    .Do(changes =>
                    {
                        if (seeded) return;
                        seeded = true;
                        initialLastIndex = GetLastIndex(changes.Select(change => change.Current.SortIndex));
                    })
                    .Transform(item => ToLoadOrderItemModel(item, sortOrderVariety, topMostIndexObservable, bottomMostIndexObservable, lastIndexObservable, initialLastIndex, _connection, _thumbnailLoader));
            })
            .SubscribeOn(RxApp.TaskpoolScheduler);

        static int GetLastIndex(IEnumerable<int> sortIndexes) => sortIndexes.DefaultIfEmpty(0).Max();
    }

    private static CompositeItemModel<ISortItemKey> ToLoadOrderItemModel(
        IReactiveSortItem reactiveSortItem,
        ISortOrderVariety sortOrderVariety,
        R3.Observable<int> topMostIndexObservable,
        R3.Observable<int> bottomMostIndexObservable,
        R3.Observable<int> lastIndexObservable,
        int initialLastIndex,
        IConnection connection,
        Lazy<IResourceLoader<EntityId, Bitmap>> thumbnailLoader)
    {
        var compositeModel = new CompositeItemModel<ISortItemKey>(reactiveSortItem.Key);

        // DisplayName
        compositeModel.Add(LoadOrderColumns.DisplayNameColumn.DisplayNameComponentKey,
            new StringComponent(reactiveSortItem.DisplayName, reactiveSortItem.WhenAnyValue(item => item.DisplayName)));
        
        // Thumbnail
        if (reactiveSortItem.ModGroupId.HasValue)
        {
            if (LoadoutItemGroup.Load(connection.Db, reactiveSortItem.ModGroupId.Value).TryGetAsLibraryLinkedLoadoutItem(out var linkedItem))
            {
                if (linkedItem.LibraryItem.TryGetAsNexusModsLibraryItem(out var nexusLibraryItem))
                {
                    compositeModel.Add(LoadOrderColumns.DisplayNameColumn.ImageComponentKey,
                        ImageComponent.FromPipeline(thumbnailLoader.Value, nexusLibraryItem.ModPageMetadata, initialValue: ImagePipelines.ModPageThumbnailFallback));
                }
            }
        }

        // ModName
        compositeModel.Add(LoadOrderColumns.ModNameColumn.ModNameComponentKey,
            new StringComponent(reactiveSortItem.ModName, reactiveSortItem.WhenAnyValue(item => item.ModName)));

        // IsActive
        compositeModel.Add(LoadOrderColumns.IsActiveComponentKey,
            new ValueComponent<bool>(reactiveSortItem.IsActive, reactiveSortItem.WhenAnyValue(item => item.IsActive)));

        // Switch, flags and masters: only for rows that describe them. The row is replaced when its switch
        // changes, so none of these follow the item.
        var details = reactiveSortItem as ISortItemDetails;
        if (details is not null)
        {
            if (sortOrderVariety is IToggleableSortOrderVariety)
            {
                // A row can be movable and still have a locked switch (a game-installed plugin follows its group), so
                // the switch looks at the reason as well; the drag handle below keeps reading IsLocked alone.
                var toolTip = details.LockReason == SortItemLockReason.GameInstalledGroup
                    ? Language.LoadOrder_GameInstalledLockedToolTip
                    : Language.LoadOrder_LockedToolTip;
                compositeModel.Add(LoadOrderColumns.EnabledColumn.EnabledComponentKey,
                    new LoadOrderToggleComponent(details.IsEnabled, details.IsLocked || details.LockReason is not null, toolTip));
            }

            var mastersToolTip = details.Masters.Count == 0
                ? Language.LoadOrder_NoMastersToolTip
                : string.Format(Language.LoadOrder_MastersToolTip, string.Join(Environment.NewLine, details.Masters));
            compositeModel.Add(LoadOrderColumns.DetailsColumn.DetailsComponentKey,
                new LoadOrderDetailsComponent(details.Flags, mastersToolTip));
        }

        // SortIndex
        var sortIndexObservable = reactiveSortItem.WhenAnyValue(item => item.SortIndex).ToObservable();
        
        var canExecuteMoveUp = R3.Observable.CombineLatest(
            sortIndexObservable,
            topMostIndexObservable,
            (sortIndex, topMostIndex) => sortIndex != topMostIndex
        );
        var canExecuteMoveDown = R3.Observable.CombineLatest(
            sortIndexObservable,
            bottomMostIndexObservable,
            (sortIndex, bottomMost) => sortIndex != bottomMost
        );
        
        
        // The column shows the load position, 1 at the top of the page and the winner last, whichever index wins in the
        // stored order; the raw SortIndex stays on the component for sorting and the move buttons.
        var behavior = sortOrderVariety.IndexOverrideBehavior;
        var displayIndexObservable = R3.Observable.CombineLatest(
            sortIndexObservable,
            lastIndexObservable,
            (sortIndex, lastIndex) => LoadPositionText(sortIndex, lastIndex, behavior)
        );

        compositeModel.Add(LoadOrderColumns.IndexColumn.IndexComponentKey,
            new SharedComponents.IndexComponent(
                new ValueComponent<int>(reactiveSortItem.SortIndex, sortIndexObservable),
                new ValueComponent<string>(LoadPositionText(reactiveSortItem.SortIndex, initialLastIndex, behavior), displayIndexObservable),
                canExecuteMoveUp,
                canExecuteMoveDown,
                isLocked: details?.IsLocked ?? false
            )
        );

        return compositeModel;
    }
    
    private static string LoadPositionText(int sortIndex, int lastIndex, IndexOverrideBehavior behavior)
        => LoadOrderConvention.LoadPosition(sortIndex, lastIndex, behavior).Ordinalize();
}
