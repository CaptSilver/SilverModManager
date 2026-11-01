using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI.Helpers;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Sdk.Resources;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.MnemonicDB.Abstractions.Query;
using NuGet.Versioning;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using R3;

namespace SilverModManager.App.UI.Pages;

public class NexusModsDataProvider : ALibraryDataProvider<NexusModsLibraryItem.ReadOnly>, ILoadoutDataProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _connection;
    private readonly GameNames _gameNames;
    private readonly ModSourceResolver _sources;
    private readonly Lazy<IResourceLoader<EntityId, Bitmap>> _thumbnailLoader;

    public NexusModsDataProvider(IServiceProvider serviceProvider) : this(serviceProvider, serviceProvider.GetRequiredService<IConnection>()) { }

    private NexusModsDataProvider(IServiceProvider serviceProvider, IConnection connection) : base(connection)
    {
        _serviceProvider = serviceProvider;
        _connection = connection;
        _gameNames = new GameNames(serviceProvider.GetServices<IGameData>());
        _sources = serviceProvider.GetRequiredService<ModSourceResolver>();

        _thumbnailLoader = new Lazy<IResourceLoader<EntityId, Bitmap>>(() => ImagePipelines.GetModPageThumbnailPipeline(serviceProvider));
    }

    protected override Optional<LibraryFile.ReadOnly> AsLibraryFile(NexusModsLibraryItem.ReadOnly model)
        => model.AsLibraryItem().TryGetAsLibraryFile(out var file) ? file : Optional<LibraryFile.ReadOnly>.None;

    private static EntityId PageOf(NexusModsLibraryItem.ReadOnly item) => item.ModPageMetadataId;

    /// <summary>
    /// The game's mod pages, each with the files of it the Mods tab lists (the ones the app fetched for a Wabbajack
    /// modlist are held back). A page with no listed file has no group, so no row. The pages come from the
    /// game's items, not from the Nexus id: two games can share one id.
    /// </summary>
    private IObservable<IChangeSet<IGroup<NexusModsLibraryItem.ReadOnly, EntityId, EntityId>, EntityId>> ObserveShownPages(LibraryFilter libraryFilter)
        => ObserveShownFiles(libraryFilter.Game.GameId).Group(PageOf);

    private IObservable<IChangeSet<NexusModsLibraryItem.ReadOnly, EntityId>> ObserveShownFiles(GameId game)
        => ObserveOwnItems(game).FilterOnObservable((_, id) => LibraryDataProviderHelper.IsShownInMods(_connection, id, game));

    public override IObservable<IChangeSet<EntityId, EntityId>> ObserveModsTabItems(GameId game)
        => ObserveShownFiles(game).Transform(static (_, id) => id);

    public override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter)
    {
        return ObserveShownPages(libraryFilter)
            .Transform(page => ToLibraryItemModel(page, libraryFilter))
            .ChangeKey(static model => model.Key);
    }

    private CompositeItemModel<EntityId> ToLibraryItemModel(IGroup<NexusModsLibraryItem.ReadOnly, EntityId, EntityId> page, LibraryFilter libraryFilter)
    {
        var modPage = NexusModsModPageMetadata.Load(_connection.Db, page.Key);
        var libraryItems = page.Cache.Connect().RefCount();

        var linkedLoadoutItemsObservable = libraryItems.MergeManyChangeSets(libraryItem => LibraryDataProviderHelper.GetLinkedLoadoutItems(_connection, libraryFilter, libraryItem.Id));

        var hasChildrenObservable = libraryItems.IsNotEmpty();
        var childrenObservable = libraryItems.Transform(libraryItem => ToLibraryItemModel(libraryItem, libraryFilter));

        var parentItemModel = new CompositeItemModel<EntityId>(modPage.Id)
        {
            HasChildrenObservable = hasChildrenObservable,
            ChildrenObservable = childrenObservable,
        };
        
        parentItemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: modPage.Name));
        SharedColumns.Source.Add(parentItemModel, ModSources.Nexus);
        parentItemModel.Add(SharedColumns.Name.ImageComponentKey, ImageComponent.FromPipeline(_thumbnailLoader.Value, modPage.Id, initialValue: ImagePipelines.ModPageThumbnailFallback));

        // Size: sum of library files
        var sizeObservable = libraryItems
            .TransformImmutable(static item => LibraryFile.Size.GetOptional(item).ValueOr(() => Size.Zero))
            .ForAggregation()
            .Sum(static size => (long)size.Value)
            .Select(static size => Size.FromLong(size));

        parentItemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(
            initialValue: Size.Zero,
            valueObservable: sizeObservable
        ));

        // Downloaded date: most recent downloaded file date
        var downloadedDateObservable = libraryItems
            .TransformImmutable(static item => item.GetCreatedAt())
            .QueryWhenChanged(query => query.Items.OptionalMaxBy(item => item).ValueOr(DateTimeOffset.MinValue));

        parentItemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(
            initialValue: modPage.GetCreatedAt(),
            valueObservable: downloadedDateObservable
        ));

        // Version: highest version number
        var currentVersionObservable = libraryItems
            .TransformImmutable(static item =>
            {
                // NOTE(erri120, sewer): simplest version parsing for now
                var rawVersion = item.FileMetadata.Version;
                _ = NuGetVersion.TryParse(rawVersion, out var parsedVersion);

                return (rawVersion, parsedVersion: Optional<NuGetVersion>.Create(parsedVersion));
            })
            .QueryWhenChanged(static query =>
            {
                var max = query.Items.OptionalMaxBy(static tuple => tuple.parsedVersion.ValueOr(new NuGetVersion(0, 0, 0)));
                if (!max.HasValue) return string.Empty;
                return max.Value.rawVersion;
            });

        parentItemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(
            initialValue: string.Empty,
            valueObservable: currentVersionObservable
        ));
        
        // Collections column
        AddRelatedCollectionsComponentToParent(libraryItems, linkedLoadoutItemsObservable, parentItemModel);
        
        // Update available
        var pageItems = page.Cache.Items.Select(static item => item.AsLibraryItem()).ToArray();
        var pageUpdate = ObserveUpdate(pageItems);
        var currentUpdateVersionObservable = pageUpdate
            .Select(static optional => optional.HasValue ? optional.Value.CurrentVersion : "")
            .OnUI();
        var newestVersionObservable = pageUpdate
            .Select(static optional => optional.Convert(static update => update.NewestVersion))
            .OnUI();

        parentItemModel.AddObservable(
            key: LibraryColumns.ItemVersion.NewVersionComponentKey,
            observable: newestVersionObservable,
            componentFactory: (valueObservable, initialValue) => new LibraryComponents.NewVersionAvailable(
                currentVersion: new VersionComponent(
                    initialValue: string.Empty,
                    valueObservable: currentUpdateVersionObservable
                ),
                newVersion: initialValue,
                newVersionObservable: valueObservable
            )
        );

        // Update button
        parentItemModel.AddObservable(
            key: LibraryColumns.Actions.UpdateComponentKey,
            observable: pageUpdate,
            componentFactory: static (valueObservable, initialValue) => new LibraryComponents.UpdateAction(
                initialValue,
                valueObservable
            )
        );

        LibraryDataProviderHelper.AddInstalledDateComponent(parentItemModel, linkedLoadoutItemsObservable);

        var matchesObservable = libraryItems
            .TransformOnObservable(libraryItem => LibraryDataProviderHelper.GetLinkedLoadoutItems(_connection, libraryFilter, libraryItem.Id).IsNotEmpty())
            .QueryWhenChanged(query =>
            {
                var (numInstalled, numTotal) = (0, 0);
                foreach (var isInstalled in query.Items)
                {
                    numInstalled += isInstalled ? 1 : 0;
                    numTotal++;
                }

                return new MatchesData(numInstalled, numTotal);
            });
        
        
        parentItemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(libraryItems.TransformImmutable(static x => x.AsLibraryItem().LibraryItemId)));
        LibraryDataProviderHelper.AddInstallActionComponent(parentItemModel, matchesObservable);
        LibraryDataProviderHelper.AddViewChangelogActionComponent(parentItemModel);
        LibraryDataProviderHelper.AddViewModPageActionComponent(parentItemModel);

        // Note(sewer):
        // The hide action stays on a row whose updates are all hidden, or the user would have no way to show them
        // again; the source reports its state from the unfiltered updates.
        var pageItemsNow = libraryItems.QueryWhenChanged(static query => (IReadOnlyList<LibraryItem.ReadOnly>)query.Items.Select(static item => item.AsLibraryItem()).ToArray());
        LibraryDataProviderHelper.AddHideUpdatesActionComponent(parentItemModel, SourceOf(pageItems[0]), pageItemsNow);
        
        LibraryDataProviderHelper.AddDeleteItemActionComponent(parentItemModel);
        LibraryDataProviderHelper.AddDestinationComponents(parentItemModel, _connection, libraryFilter.Game.GameId, _gameNames);

        return parentItemModel;
    }

    private IModSource SourceOf(LibraryItem.ReadOnly item)
        => _sources.For(item) ?? throw new InvalidOperationException($"No mod source owns the Nexus Mods item {item.Id}.");

    private IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
        => _sources.For(item)?.ObserveUpdate(item) ?? System.Reactive.Linq.Observable.Return(Optional<ModUpdate>.None);

    // The page's items only name the page; the update is the page's, whichever of its items change later.
    private IObservable<Optional<ModUpdate>> ObserveUpdate(IReadOnlyList<LibraryItem.ReadOnly> pageItems)
        => pageItems.Count > 0 && _sources.For(pageItems[0]) is { } source
            ? source.ObserveUpdate(pageItems)
            : System.Reactive.Linq.Observable.Return(Optional<ModUpdate>.None);

    private CompositeItemModel<EntityId> ToLibraryItemModel(NexusModsLibraryItem.ReadOnly libraryItem, LibraryFilter libraryFilter)
    {
        var linkedLoadoutItemsObservable = LibraryDataProviderHelper
            .GetLinkedLoadoutItems(_connection, libraryFilter, libraryItem.Id)
            .RefCount();

        var fileMetadata = libraryItem.FileMetadata;

        var itemModel = new CompositeItemModel<EntityId>(libraryItem.Id);

        itemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(libraryItem.AsLibraryItem()));
        itemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: fileMetadata.Name));
        SharedColumns.Source.Add(itemModel, ModSources.Nexus);
        itemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(value: libraryItem.GetCreatedAt()));
        itemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(value: fileMetadata.Version));

        if (libraryItem.FileMetadata.Size.TryGet(out var size))
            itemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(value: size));

        LibraryDataProviderHelper.AddInstalledDateComponent(itemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddInstallActionComponent(itemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddViewChangelogActionComponent(itemModel);
        LibraryDataProviderHelper.AddViewModPageActionComponent(itemModel);
        
        // Collections column
        AddRelatedCollectionsComponentToChild(libraryItem, linkedLoadoutItemsObservable, itemModel);

        // Update available
        var fileUpdate = ObserveUpdate(libraryItem.AsLibraryItem());
        var newestVersionObservable = fileUpdate
            .Select(static optional => optional.Convert(static update => update.NewestVersion))
            .OnUI();

        itemModel.AddObservable(
            key: LibraryColumns.ItemVersion.NewVersionComponentKey,
            observable: newestVersionObservable,
            componentFactory: (valueObservable, initialValue) => new LibraryComponents.NewVersionAvailable(
                currentVersion: new VersionComponent(value: libraryItem.FileMetadata.Version),
                newVersion: initialValue,
                newVersionObservable: valueObservable
            )
        );

        // Update button
        itemModel.AddObservable(
            key: LibraryColumns.Actions.UpdateComponentKey,
            observable: fileUpdate,
            componentFactory: static (valueObservable, initialValue) => new LibraryComponents.UpdateAction(
                initialValue,
                valueObservable
            )
        );

        LibraryDataProviderHelper.AddHideUpdatesActionComponent(itemModel, SourceOf(libraryItem.AsLibraryItem()), [libraryItem.AsLibraryItem()]);
        
        LibraryDataProviderHelper.AddDeleteItemActionComponent(itemModel);
        LibraryDataProviderHelper.AddDestinationComponents(itemModel, _connection, libraryFilter.Game.GameId, _gameNames);

        return itemModel;
    }

    private void AddRelatedCollectionsComponentToChild(
        NexusModsLibraryItem.ReadOnly libraryItem,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable,
        CompositeItemModel<EntityId> itemModel)
    {
        var relatedDownloadedCollectionsObservable = _connection
            .ObserveDatoms(CollectionDownloadNexusMods.FileMetadataId, libraryItem.FileMetadataId)
            .AsEntityIds()
            .Distinct()
            .Transform(datom => SilverModManager.Abstractions.NexusModsLibrary.Models.CollectionDownload.Load(_connection.Db, datom.E).CollectionRevision.Collection)
            .ChangeKey(collection => collection.Id);

        LibraryDataProviderHelper.AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable, relatedDownloadedCollectionsObservable);
    }

    private void AddRelatedCollectionsComponentToParent(
        IObservable<IChangeSet<NexusModsLibraryItem.ReadOnly, EntityId>> libraryItems,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable,
        CompositeItemModel<EntityId> itemModel)
    {
        var relatedDownloadedCollectionsObservable = libraryItems
            .MergeManyChangeSets(libraryItem =>
                _connection.ObserveDatoms(CollectionDownloadNexusMods.FileMetadataId, libraryItem.FileMetadataId)
                    .AsEntityIds()
                    .Distinct()
                    .Transform(datom => SilverModManager.Abstractions.NexusModsLibrary.Models.CollectionDownload
                        .Load(_connection.Db, datom.E)
                        .CollectionRevision.Collection
                    )
                    .ChangeKey(collection => collection.Id)
                    .Distinct()
            );

        LibraryDataProviderHelper.AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable, relatedDownloadedCollectionsObservable);
    }

    /// <summary>The loadout's game's mod pages, each with the files of it that are linked into the loadout.</summary>
    private IObservable<IChangeSet<IGroup<NexusModsLibraryItem.ReadOnly, EntityId, EntityId>, EntityId>> FilterLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return ObserveOwnItemsForLoadout(loadoutFilter.LoadoutId)
            .FilterOnObservable((_, itemId) => _connection
                .ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItemId, itemId)
                .AsEntityIds()
                .FilterInStaticLoadout(_connection, loadoutFilter)
                .IsNotEmpty())
            .Group(PageOf);
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).Transform(page =>
        {
            var modPage = NexusModsModPageMetadata.Load(_connection.Db, page.Key);
            var linkedItemsObservable = page.Cache.Connect()
                // NOTE(erri120): DynamicData 9.0.4 is broken for value types because it uses ReferenceEquals. Temporary workaround is a custom equality comparer.
                .MergeManyChangeSets((_, e) => _connection.ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItemId, e).AsEntityIds(), equalityComparer: DatomEntityIdEqualityComparer.Instance)
                .FilterInStaticLoadout(_connection, loadoutFilter)
                .Transform(datom => LoadoutItem.Load(_connection.Db, datom.E))
                .RefCount();

            var hasChildrenObservable = linkedItemsObservable.IsNotEmpty();
            var childrenObservable = linkedItemsObservable.Transform(loadoutItem => ToNexusChildLoadoutItemModel(_connection, loadoutItem));

            var parentItemModel = new CompositeItemModel<EntityId>(modPage.Id)
            {
                HasChildrenObservable = hasChildrenObservable,
                ChildrenObservable = childrenObservable,
            };

            parentItemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: modPage.Name));
            SharedColumns.Source.Add(parentItemModel, ModSources.Nexus);
            parentItemModel.Add(SharedColumns.Name.ImageComponentKey, ImageComponent.FromPipeline(_thumbnailLoader.Value, modPage.Id, initialValue: ImagePipelines.ModPageThumbnailFallback));

            LoadoutDataProviderHelper.AddDateComponent(parentItemModel, modPage.GetCreatedAt(), linkedItemsObservable);
            LoadoutDataProviderHelper.AddCollections(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddParentCollectionsDisabled(_connection, parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddMixLockedAndParentDisabled(_connection, parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddLockedEnabledStates(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddEnabledStateToggle(_connection, parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddLoadoutItemIds(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddViewModPageActionComponent(parentItemModel, isEnabled: true);
            LoadoutDataProviderHelper.AddViewModFilesActionComponent(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddUninstallItemComponent(parentItemModel, linkedItemsObservable);
            LoadoutDataProviderHelper.AddAllowProtectedPathsAction(_connection, parentItemModel, linkedItemsObservable);

            return parentItemModel;
        }).ChangeKey(static model => model.Key);
    }
    
    private static CompositeItemModel<EntityId> ToNexusChildLoadoutItemModel(IConnection connection, LoadoutItem.ReadOnly loadoutItem)
    {
        var childModel = LoadoutDataProviderHelper.ToChildItemModel(connection, loadoutItem);
        SharedColumns.Source.Add(childModel, ModSources.Nexus);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(childModel, isEnabled: true);
        return childModel;
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).QueryWhenChanged(static query => query.Count).Prepend(0);
    }
}

internal class DatomEntityIdEqualityComparer : IEqualityComparer<Datom>
{
    public static readonly IEqualityComparer<Datom> Instance = new DatomEntityIdEqualityComparer();

    public bool Equals(Datom x, Datom y)
    {
        return x.E == y.E;
    }

    public int GetHashCode(Datom obj)
    {
        return obj.E.GetHashCode();
    }
}
