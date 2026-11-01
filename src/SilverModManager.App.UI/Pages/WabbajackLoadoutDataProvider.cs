using System.Reactive.Linq;
using DynamicData;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// Lists the mods a Wabbajack modlist install wrote: plain groups parented to the modlist's group, holding files and
/// carrying no library link. The library-linked providers never see them. A child that does carry a library link
/// belongs to the provider that owns that link, so it is left out here and never counted twice.
/// </summary>
[UsedImplicitly]
public class WabbajackLoadoutDataProvider : ILoadoutDataProvider
{
    private readonly IConnection _connection;

    public WabbajackLoadoutDataProvider(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    /// <summary>
    /// A collection's page lists that collection's mods when the collection is a modlist; any other collection is
    /// another provider's. Without a collection, every modlist of the loadout contributes.
    /// </summary>
    private IObservable<IChangeSet<Datom, EntityId>> ObserveModGroups(LoadoutFilter loadoutFilter)
    {
        if (!loadoutFilter.CollectionGroupId.HasValue)
        {
            return _connection.ObserveDatoms(WabbajackModlistLoadoutGroup.Modlist)
                .AsEntityIds()
                .FilterInStaticLoadout(_connection, loadoutFilter)
                .MergeManyChangeSets((modlist, _) => ObserveChildGroups(modlist.E));
        }

        var collection = loadoutFilter.CollectionGroupId.Value.Value;
        var isModlist = WabbajackModlistLoadoutGroup.Load(_connection.Db, collection).IsValid();
        return isModlist
            ? ObserveChildGroups(collection)
            : Observable.Empty<IChangeSet<Datom, EntityId>>();
    }

    /// <summary>
    /// Child groups arrive as parent datoms, one change per group, so thousands added by an install cost one check
    /// each and never a re-query of the rest.
    /// </summary>
    private IObservable<IChangeSet<Datom, EntityId>> ObserveChildGroups(EntityId modlistGroup)
    {
        return _connection.ObserveDatoms(LoadoutItem.ParentId, modlistGroup)
            .AsEntityIds()
            .Filter(datom => IsUnlinkedGroup(datom.E));
    }

    private bool IsUnlinkedGroup(EntityId id)
    {
        var db = _connection.Db;
        return LoadoutItemGroup.Load(db, id).IsValid() && !LibraryLinkedLoadoutItem.Load(db, id).IsValid();
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return ObserveModGroups(loadoutFilter)
            .Transform(datom => ToItemModel(LoadoutItem.Load(_connection.Db, datom.E)));
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return ObserveModGroups(loadoutFilter).QueryWhenChanged(static query => query.Count).Prepend(0);
    }

    /// <summary>
    /// One flat row per mod. Removing a mod alone is left off: the install keeps writing to its groups until it
    /// finishes, and the modlist is removed whole from its own page.
    /// </summary>
    private CompositeItemModel<EntityId> ToItemModel(LoadoutItem.ReadOnly loadoutItem)
    {
        var model = new CompositeItemModel<EntityId>(loadoutItem.Id);

        model.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: loadoutItem.Name));
        SharedColumns.Source.Add(model, ModSources.Wabbajack);
        model.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));
        model.Add(SharedColumns.InstalledDate.ComponentKey, new DateComponent(value: loadoutItem.GetCreatedAt()));
        model.Add(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey, new LoadoutComponents.LoadoutItemIds(itemId: loadoutItem));
        model.Add(LoadoutColumns.EnabledState.ViewModFilesComponentKey, new SharedComponents.ViewModFilesAction(isEnabled: true));
        model.Add(LoadoutColumns.EnabledState.UninstallItemComponentKey, new SharedComponents.UninstallItemAction(isEnabled: false));

        LoadoutDataProviderHelper.AddViewModPageActionComponent(model, isEnabled: false);
        LoadoutDataProviderHelper.AddCollection(_connection, model, loadoutItem);
        LoadoutDataProviderHelper.AddParentCollectionDisabled(_connection, model, loadoutItem);
        LoadoutDataProviderHelper.AddEnabledStateToggle(_connection, model, loadoutItem);
        LoadoutDataProviderHelper.AddAllowProtectedPathsAction(_connection, model, loadoutItem);

        return model;
    }
}
