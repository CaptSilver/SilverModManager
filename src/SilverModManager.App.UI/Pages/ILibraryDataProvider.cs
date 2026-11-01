using System.Reactive.Linq;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.UpdateFilters;
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
    /// Whether the Mods tab lists the library file under <paramref name="game"/>. One the app fetched for a Wabbajack
    /// modlist of that game is that list's own download and stays out of the way until the user installs it into a
    /// loadout of the game, which makes it theirs. A list for another game does not hide it here: a file held for
    /// Skyrim SE that a Skyrim VR list adopts stays visible under Skyrim SE.
    /// </summary>
    public static IObservable<bool> IsShownInMods(IConnection connection, EntityId libraryFile, GameId game)
    {
        // FetchedFiles is a collection, so it has no reference-attribute overload; the back-reference slice works for any reference.
        var fetchedFiles = connection.AttributeCache.GetAttributeId(WabbajackModlistArchive.FetchedFiles.Id);
        var fetched = connection.ObserveDatoms(SliceDescriptor.Create(fetchedFiles, libraryFile))
            .AsEntityIds()
            .FilterImmutable(row => IsRowOfListFor(connection.Db, row.E, game))
            .IsNotEmpty();
        var installed = ObserveInstalledForGame(connection, libraryFile, game);
        return fetched.CombineLatest(installed, static (isFetched, isInstalled) => !isFetched || isInstalled).DistinctUntilChanged();
    }

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

    // A list for a game the app has no title for belongs to no game in particular, so it counts for whichever game is
    // asked. So does a row whose list is gone: the file was fetched for a list, and hiding it is the old behaviour.
    private static bool IsRowOfListFor(IDb db, EntityId archiveRow, GameId game)
    {
        var list = WabbajackModlistLibraryFile.Load(db, WabbajackModlistArchive.Load(db, archiveRow).ModlistId);
        return !list.IsValid() || WabbajackGames.GameIdFor(list.GameType) is not { } listGame || listGame == game;
    }

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

    public static void AddHideUpdatesActionComponent(
        CompositeItemModel<EntityId> itemModel,
        IObservable<Optional<ModUpdateOnPage>> unfilteredFileUpdateObservable,
        IModUpdateFilterService filterService,
        Observable<bool> isVisibleObservable)
    {
        // Note(sewer):
        // Determine if the current file is hidden by checking if any of the newer
        // files are hidden in an unfiltered state.
        //
        // The reason we use the unfiltered observable is that we also need to capture
        // cases where *there are no updates*; so we can appropriately disable the button.
        // With a filtered view we *can't do that*, because we may not be seeing updates
        
        // The 'newest' in this context is unfiltered, and is the target we would update to,
        // so this is the correct file to check.
        var isHiddenObservable = unfilteredFileUpdateObservable
            // Because our view is unfiltered, we must listen to filter changes ourselves.
            .CombineLatest(filterService.FilterTrigger.StartWith(System.Reactive.Unit.Default), (optional, _) => optional)
            .Select(optional =>
            {
                if (!optional.HasValue)
                    return false;
                    
                // Check if all newer files are hidden to determine if this update is hidden.
                return optional.Value.NewerFiles.All(x => filterService.IsFileHidden(x.Uid));
            })
            .ToObservable();
        
        // For individual files: count is always 1
        var itemCountObservable = Observable.Return(1);
        
        // Disable the button if there are no newer files (when unfiltered).
        // This means there are no updates, so show/hide is meaningless.
        var isEnabledObservable = unfilteredFileUpdateObservable
            .Select(optional => optional.HasValue)
            .ToObservable();

        itemModel.Add(LibraryColumns.Actions.HideUpdatesComponentKey, new LibraryComponents.HideUpdatesAction(isHiddenObservable, itemCountObservable, isEnabledObservable, isVisibleObservable));
    }

    private record struct FileUpdateDetails(int NumUpdatesHidden, int NumUpdatesShown);
    public static void AddHideUpdatesActionComponent(
        CompositeItemModel<EntityId> itemModel,
        IObservable<Optional<ModUpdatesOnModPage>> unfilteredUpdatedOnModPage,
        IModUpdateFilterService modUpdateFilterService,
        Observable<bool> isVisibleObservable)
    {
        // Note(sewer):
        //
        // Same note as above.
        //
        // The reason we use the unfiltered observable is that we also need to capture
        // cases where *there are no updates*; so we can appropriately disable the button.
        // With a filtered view we *can't do that*, because we may not be seeing updates
        //
        // In addition, we must also be able to capture cases where *all* updates are hidden,
        // not just where they are visible, so we can show proper counters in the UI, as per
        // the design spec. Therefore, we must do the filtering ourselves 😉
        
        // Trigger refresh either if mods on page change, or filter is updated.
        var currentDetails = unfilteredUpdatedOnModPage
            .CombineLatest(modUpdateFilterService.FilterTrigger.StartWith(System.Reactive.Unit.Default), (files, _) => files)
            .Select(files =>
            {
                if (!files.HasValue)
                    return new FileUpdateDetails(0, 0);
                
                var numHidden = 0;
                var numShown = 0;
                foreach (var updateOnPage in files.Value.FileMappings)
                {
                    var areAllUpdatesHidden = updateOnPage.NewerFiles.All(newer => modUpdateFilterService.IsFileHidden(newer.Uid));
                    if (areAllUpdatesHidden)
                        numHidden++;
                    else
                        numShown++;
                }

                return new FileUpdateDetails(numHidden, numShown);
            });

        // Note(sewer):
        // Behaviour per captainsandypants (Slack).
        // 'If any children have updates set to hidden, then the parent should have "Show updates" as the menu item.
        // When selected, this will set all children to show updates.'
        var isHiddenObservable = currentDetails.Select(details => details.NumUpdatesHidden > 0)
            .ToObservable();
        
        // Always show the number of files under the 'Mod Page' item, as this is the number of items
        // affected by the 'Show updates' action.
        var itemCountObservable = currentDetails.Select(details => details.NumUpdatesHidden > 0 ? details.NumUpdatesHidden : details.NumUpdatesShown)
            .ToObservable();
        
        // ReSharper disable once MergeIntoPattern
        var isEnabledObservable = unfilteredUpdatedOnModPage
            .Select(optional => optional.HasValue && optional.Value.HasAnyUpdates)
            .ToObservable();
        
        itemModel.Add(LibraryColumns.Actions.HideUpdatesComponentKey, new LibraryComponents.HideUpdatesAction(isHiddenObservable, itemCountObservable, isEnabledObservable, isVisibleObservable));
    }
}
