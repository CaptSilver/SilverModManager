using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Resources;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// Lists every child group of a modlist group, library-linked or not: the install writes each mod as a group
/// parented to the modlist's group and links it to the archive most of its files came from. The linked providers
/// leave those groups to this one through <see cref="LoadoutDataProviderHelper.ObserveLinkedGroups"/>, so a mod is
/// never counted twice.
/// </summary>
[UsedImplicitly]
public class WabbajackLoadoutDataProvider : ILoadoutDataProvider
{
    private readonly IConnection _connection;
    private readonly ModSourceResolver _sources;
    private readonly Lazy<IResourceLoader<EntityId, Bitmap>> _thumbnails;

    public WabbajackLoadoutDataProvider(IServiceProvider serviceProvider)
        : this(serviceProvider, new Lazy<IResourceLoader<EntityId, Bitmap>>(() => ImagePipelines.GetModPageThumbnailPipeline(serviceProvider)))
    {
    }

    public WabbajackLoadoutDataProvider(IServiceProvider serviceProvider, Lazy<IResourceLoader<EntityId, Bitmap>> thumbnails)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _sources = serviceProvider.GetRequiredService<ModSourceResolver>();
        _thumbnails = thumbnails;
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
            .Filter(datom => IsModGroup(datom.E));
    }

    private bool IsModGroup(EntityId id) => LoadoutItemGroup.Load(_connection.Db, id).IsValid();

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
    /// finishes, and the modlist is removed whole from its own page. The thumbnail and "View mod page" follow the
    /// mod's library link, which the install or the backfill may write after the row exists.
    /// </summary>
    private CompositeItemModel<EntityId> ToItemModel(LoadoutItem.ReadOnly loadoutItem)
    {
        var model = new CompositeItemModel<EntityId>(loadoutItem.Id);

        model.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: loadoutItem.Name));
        SharedColumns.Source.Add(model, ModSources.Wabbajack);
        model.Add(SharedColumns.Name.ImageComponentKey, LinkedItemImages.ObserveForGroup(_connection, _thumbnails.Value, loadoutItem.Id));
        model.Add(SharedColumns.InstalledDate.ComponentKey, new DateComponent(value: loadoutItem.GetCreatedAt()));
        model.Add(LoadoutColumns.EnabledState.LoadoutItemIdsComponentKey, new LoadoutComponents.LoadoutItemIds(itemId: loadoutItem));
        model.Add(LoadoutColumns.EnabledState.ViewModFilesComponentKey, new SharedComponents.ViewModFilesAction(isEnabled: true));
        model.Add(LoadoutColumns.EnabledState.UninstallItemComponentKey, new SharedComponents.UninstallItemAction(isEnabled: false));

        model.Add(LoadoutColumns.EnabledState.ViewModPageComponentKey, ViewModPage(loadoutItem.Id));
        LoadoutDataProviderHelper.AddCollection(_connection, model, loadoutItem);
        LoadoutDataProviderHelper.AddParentCollectionDisabled(_connection, model, loadoutItem);
        LoadoutDataProviderHelper.AddEnabledStateToggle(_connection, model, loadoutItem);
        LoadoutDataProviderHelper.AddAllowProtectedPathsAction(_connection, model, loadoutItem);

        return model;
    }

    private SharedComponents.ViewModPageAction ViewModPage(EntityId group)
    {
        var linked = LinkedItemImages.ObserveLinkedItem(_connection, group).Select(HasPage);
        return new SharedComponents.ViewModPageAction(HasPage(LinkedItem(group)), linked);
    }

    private Optional<EntityId> LinkedItem(EntityId group)
    {
        var link = LibraryLinkedLoadoutItem.Load(_connection.Db, group);
        return link.IsValid() && LibraryLinkedLoadoutItem.LibraryItem.TryGetValue(link, out var item)
            ? Optional<EntityId>.Create(item)
            : Optional<EntityId>.None;
    }

    private bool HasPage(Optional<EntityId> link) =>
        link.HasValue
        && ModSourcePages.Find(_connection.Db, _sources, [LibraryItemId.From(link.Value)], static (source, item) => source.PageUri(item)) is not null;
}
