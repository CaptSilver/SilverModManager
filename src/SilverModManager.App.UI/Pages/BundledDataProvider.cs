using System.Reactive.Linq;
using DynamicData;
using DynamicData.Aggregation;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Query;
using UIObservableExtensions = SilverModManager.App.UI.Extensions.ObservableExtensions;

namespace SilverModManager.App.UI.Pages;

[UsedImplicitly]
public class BundledDataProvider : ILoadoutDataProvider
{
    private readonly IConnection _connection;

    public BundledDataProvider(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    private IObservable<IChangeSet<Datom, EntityId>> FilterLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return _connection.ObserveDatoms(NexusCollectionBundledLoadoutGroup.PrimaryAttribute)
            .AsEntityIds()
            .FilterInStaticLoadout(_connection, loadoutFilter);
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter)
            .TransformImmutable(datom => NexusCollectionBundledLoadoutGroup.Load(_connection.Db, datom.E))
            .Transform(ToLoadoutItemModel);
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).QueryWhenChanged(static query => query.Count).Prepend(0);
    }

    private CompositeItemModel<EntityId> ToLoadoutItemModel(NexusCollectionBundledLoadoutGroup.ReadOnly item)
    {
        var loadoutItem = item.AsNexusCollectionItemLoadoutGroup().AsLoadoutItemGroup().AsLoadoutItem();

        var childrenObservable = UIObservableExtensions.ReturnFactory(() =>
            new ChangeSet<CompositeItemModel<EntityId>, EntityId>(
                [
                    new Change<CompositeItemModel<EntityId>, EntityId>(ChangeReason.Add, item.Id, ToBundledChildLoadoutItemModel(_connection, loadoutItem)),
                ]
            )
        );
        var hasChildrenObservable = childrenObservable.IsNotEmpty();
        
        var parentItemModel = new CompositeItemModel<EntityId>(item.Id)
        {
            HasChildrenObservable = hasChildrenObservable,
            ChildrenObservable = childrenObservable,
        };

        parentItemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: item.AsNexusCollectionItemLoadoutGroup().AsLoadoutItemGroup().AsLoadoutItem().Name));
        SharedColumns.Source.Add(parentItemModel, ModSources.Bundled);
        parentItemModel.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));
        parentItemModel.Add(SharedColumns.InstalledDate.ComponentKey, new DateComponent(value: item.GetCreatedAt()));
        parentItemModel.Add(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey, new LoadoutComponents.LoadoutItemIds(loadoutItem));
        parentItemModel.Add(LoadoutColumns.EnabledState.ViewModFilesComponentKey, new SharedComponents.ViewModFilesAction(isEnabled: true));

        
        LoadoutDataProviderHelper.AddCollection(_connection, parentItemModel, loadoutItem);
        LoadoutDataProviderHelper.AddParentCollectionDisabled(_connection, parentItemModel, loadoutItem);
        LoadoutDataProviderHelper.AddLockedEnabledState(parentItemModel, loadoutItem);
        LoadoutDataProviderHelper.AddEnabledStateToggle(_connection, parentItemModel, loadoutItem);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(parentItemModel, isEnabled: false);
        LoadoutDataProviderHelper.AddUninstallItemComponent(parentItemModel, loadoutItem);
        LoadoutDataProviderHelper.AddAllowProtectedPathsAction(_connection, parentItemModel, loadoutItem);

        return parentItemModel;
    }
    
    private static CompositeItemModel<EntityId> ToBundledChildLoadoutItemModel(IConnection connection, LoadoutItem.ReadOnly loadoutItem)
    {
        var childModel = LoadoutDataProviderHelper.ToChildItemModel(connection, loadoutItem);
        SharedColumns.Source.Add(childModel, ModSources.Bundled);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(childModel, isEnabled: false);
        return childModel;
    }
}
