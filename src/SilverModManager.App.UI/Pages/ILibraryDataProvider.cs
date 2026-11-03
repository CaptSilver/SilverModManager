using System.Reactive.Linq;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack;
using SilverModManager.Wabbajack.Models;
using R3;
using Observable = R3.Observable;

namespace SilverModManager.App.UI.Pages;

public interface ILibraryDataProvider
{
    IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter);

    /// <summary>
    /// The ids of the library items this provider lists on the Mods tab for the game (its own items plus the
    /// unassigned ones), whatever the row they end up in. The Library badge counts the union over every provider.
    /// A provider with nothing on the Mods tab returns an empty set.
    /// </summary>
    IObservable<IChangeSet<EntityId, EntityId>> ObserveModsTabItems(GameId game);

    /// <summary>
    /// Returns all library files for the given game.
    /// </summary>
    LibraryFile.ReadOnly[] GetAllFiles(GameId gameId, IDb? db = null);
}

public record LibraryFilter(LoadoutId LoadoutId, IGameData Game);

public static class LibraryDataProviderHelper
{
    /// <summary>
    /// Whether the library file is one the app fetched for a Wabbajack modlist. Such a file is listed on the Mods tab
    /// like any other, with Wabbajack as its source when nothing more specific (a Nexus page) says where it came from.
    /// </summary>
    public static IObservable<bool> ObserveFetchedForModlist(IConnection connection, EntityId libraryFile)
    {
        // FetchedFiles is a collection, so it has no reference-attribute overload; the back-reference slice works for any reference.
        var fetchedFiles = connection.AttributeCache.GetAttributeId(WabbajackModlistArchive.FetchedFiles.Id);
        return connection.ObserveDatoms(SliceDescriptor.Create(fetchedFiles, libraryFile))
            .AsEntityIds()
            .IsNotEmpty()
            .DistinctUntilChanged();
    }

    /// <inheritdoc cref="ObserveFetchedForModlist"/>
    public static bool IsFetchedForModlist(IDb db, EntityId libraryFile)
        => db.Datoms(WabbajackModlistArchive.FetchedFiles, libraryFile).Any();

    /// <summary>
    /// Whether a loadout of <paramref name="game"/> has the library item installed. Any loadout of the game counts, not one
    /// loadout: the destination invariant is per game.
    /// </summary>
    public static IObservable<bool> ObserveInstalledForGame(IConnection connection, EntityId libraryItem, GameId game)
        => connection.ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItem, libraryItem)
            .AsEntityIds()
            .FilterImmutable(link => IsInLoadoutOf(connection.Db, link.E, game))
            .IsNotEmpty();

    /// <inheritdoc cref="ObserveInstalledForGame"/>
    public static bool IsInstalledForGame(IDb db, EntityId libraryItem, GameId game)
        => LibraryLinkedLoadoutItem.FindByLibraryItem(db, libraryItem)
            .Any(link => new LoadoutAcquisition(link.AsLoadoutItem().LoadoutId).Destination(db).Games.Contains(game));

    private static bool IsInLoadoutOf(IDb db, EntityId link, GameId game)
        => new LoadoutAcquisition(LoadoutItem.Load(db, link).LoadoutId).Destination(db).Games.Contains(game);

    public static IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> GetLinkedLoadoutItems(
        IConnection connection,
        LibraryFilter libraryFilter,
        LibraryItemId libraryItemId)
    {
        return connection
            .ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItemId, libraryItemId)
            .AsEntityIds()
            .Transform(datom => LoadoutItem.Load(connection.Db, datom.E))
            .FilterImmutable(loadoutItem => loadoutItem.LoadoutId.Equals(libraryFilter.LoadoutId));
    }

    public static void AddInstalledDateComponent(
        CompositeItemModel<EntityId> itemModel,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedItemsObservable)
    {
        var dateObservable = linkedItemsObservable
            .QueryWhenChanged(query =>
            {
                if (query.Count == 0) return Optional<DateTimeOffset>.None;

                return query.Items
                        .Select(static item => item.GetCreatedAt())
                        .OptionalMinBy(item => item);
            });

        itemModel.AddObservable(
            key: SharedColumns.InstalledDate.ComponentKey,
            observable: dateObservable,
            componentFactory: (valueObservable, initialValue) => new DateComponent(
                initialValue,
                valueObservable
            )
        );
    }

    /// <summary>
    /// Sorted names of the parents of the linked items. An item without a parent is in no collection and is skipped.
    /// </summary>
    public static IObservable<IChangeSet<string>> ObserveInstalledCollectionNames(IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable)
    {
        return linkedLoadoutItemsObservable
            .Filter(static item => item.HasParent())
            .Transform(item => item.Parent.AsLoadoutItem())
            .ChangeKey(coll => coll.Id)
            .Distinct()
            .Transform(collection => collection.Name)
            .SortBy(static name => name)
            .RemoveKey()
            .StartWithEmpty();
    }

    public static void AddRelatedCollectionsComponent(
        CompositeItemModel<EntityId> itemModel,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable)
    {
        AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable, 
            System.Reactive.Linq.Observable.Return(ChangeSet<CollectionMetadata.ReadOnly, EntityId>.Empty));
    }
    
    public static void AddRelatedCollectionsComponent(
        CompositeItemModel<EntityId> itemModel,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable,
        IObservable<IChangeSet<CollectionMetadata.ReadOnly, EntityId>> relatedDownloadedCollectionsObservable)
    {
        var downloadedNamesObservable = relatedDownloadedCollectionsObservable
            .Distinct()
            .Transform(collection => collection.Name)
            .SortBy(static name => name)
            .RemoveKey()
            .StartWithEmpty();
        
        var installedCollectionsObservable = ObserveInstalledCollectionNames(linkedLoadoutItemsObservable);

        // Only take downloaded entries that are not already installed
        var filteredDownloadedCollectionsObservable = downloadedNamesObservable.Except(installedCollectionsObservable);

        var hasCollectionsObservable = installedCollectionsObservable.IsEmpty()
            .CombineLatest(filteredDownloadedCollectionsObservable.IsEmpty(), 
                (installedIsEmpty, relatedIsEmpty) => !installedIsEmpty || !relatedIsEmpty)
            .DistinctUntilChanged()
            .ToObservable();

        itemModel.AddObservable(LibraryColumns.Collections.RelatedCollectionsComponentKey,
            hasCollectionsObservable,
            componentFactory: () => new LibraryComponents.RelatedCollectionsComponent(
                installedCollectionsObservable,
                filteredDownloadedCollectionsObservable
            )
        );
    }

    public static void AddInstallActionComponent(
        CompositeItemModel<EntityId> itemModel,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedItemsObservable)
    {
        itemModel.Add(LibraryColumns.Actions.InstallComponentKey, new LibraryComponents.InstallAction(
            isInstalled: new ValueComponent<bool>(
                initialValue: false,
                valueObservable: linkedItemsObservable.IsNotEmpty(),
                subscribeWhenCreated: true
            )
        ));
    }

    public static void AddInstallActionComponent(
        CompositeItemModel<EntityId> parentItemModel,
        IObservable<MatchesData> matchesObservable)
    {
        parentItemModel.Add(LibraryColumns.Actions.InstallComponentKey, new LibraryComponents.InstallAction(
            matches: new ValueComponent<MatchesData>(
                initialValue: default(MatchesData),
                valueObservable: matchesObservable,
                subscribeWhenCreated: true
            ))
        );
    }

    public static void AddViewChangelogActionComponent(
        CompositeItemModel<EntityId> itemModel,
        bool isEnabled = true)
    {
        itemModel.Add(LibraryColumns.Actions.ViewChangelogComponentKey, new LibraryComponents.ViewChangelogAction(isEnabled));
    }

    public static void AddViewModPageActionComponent(
        CompositeItemModel<EntityId> itemModel,
        bool isEnabled = true)
    {
        itemModel.Add(LibraryColumns.Actions.ViewModPageComponentKey, new SharedComponents.ViewModPageAction(isEnabled));
    }
    
    /// <summary>
    /// Adds the Game column's tag and the context-menu entries that settle which game the row is for, from the items the
    /// row's <see cref="LibraryComponents.LibraryItemIds"/> names. Called after that component is on the row.
    /// </summary>
    public static void AddDestinationComponents(
        CompositeItemModel<EntityId> itemModel,
        IConnection connection,
        GameId game,
        GameNames names)
    {
        var ids = itemModel.Get<LibraryComponents.LibraryItemIds>(LibraryColumns.Actions.LibraryItemIdsComponentKey);
        var initial = DestinationRowState.Read(connection, ids.ItemIds, game);

        // One stream feeds both components, so a row holds one database subscription for its destination state.
        var changes = DestinationRowState.Observe(connection, ids.Observe(), game).Replay(1).RefCount();

        itemModel.Add(LibraryColumns.Destination.TagComponentKey,
            new LibraryComponents.DestinationTag(new ValueComponent<DestinationRowState>(initial, changes, subscribeWhenCreated: true), names));
        itemModel.Add(LibraryColumns.Actions.DestinationActionsComponentKey,
            new LibraryComponents.DestinationActions(new ValueComponent<DestinationRowState>(initial, changes, subscribeWhenCreated: true)));
    }

    public static void AddDeleteItemActionComponent(
        CompositeItemModel<EntityId> itemModel,
        bool isEnabled = true)
    {
        itemModel.Add(LibraryColumns.Actions.DeleteItemComponentKey, new LibraryComponents.DeleteItemAction(isEnabled));
    }

    public static void AddHideUpdatesActionComponent(
        CompositeItemModel<EntityId> itemModel,
        bool isEnabled = true,
        bool isVisible = true)
    {
        // Default the observables to simple static values for now
        var isHiddenObservable = R3.Observable.Return(false);
        var itemCountObservable = R3.Observable.Return(1);
        
        itemModel.Add(LibraryColumns.Actions.HideUpdatesComponentKey, new LibraryComponents.HideUpdatesAction(isHiddenObservable, itemCountObservable, Observable.Return(isEnabled), Observable.Return(isVisible)));
    }

    /// <summary>
    /// The hide action of a row whose items belong to <paramref name="source"/>: the source says what the row shows
    /// and what the click does. A parent row passes the items it has now, so the action follows when it gains or
    /// loses one.
    /// </summary>
    public static void AddHideUpdatesActionComponent(
        CompositeItemModel<EntityId> itemModel,
        IModSource source,
        IObservable<IReadOnlyList<LibraryItem.ReadOnly>> items)
    {
        // One subscription to the source feeds all four properties. The source emits from whichever thread changed
        // the database or the filter, and the properties belong to the UI thread.
        var state = items
            .Select(source.ObserveHideUpdates)
            .Switch()
            .OnUI()
            .Replay(1)
            .RefCount()
            .ToObservable();

        itemModel.Add(LibraryColumns.Actions.HideUpdatesComponentKey, new LibraryComponents.HideUpdatesAction(
            state.Select(static x => x.IsHidden),
            state.Select(static x => x.ItemCount),
            state.Select(static x => x.IsEnabled),
            state.Select(static x => x.IsVisible),
            source.HideUpdatesLabel));
    }

    public static void AddHideUpdatesActionComponent(
        CompositeItemModel<EntityId> itemModel,
        IModSource source,
        IReadOnlyList<LibraryItem.ReadOnly> items)
        => AddHideUpdatesActionComponent(itemModel, source, System.Reactive.Linq.Observable.Return(items));
}
