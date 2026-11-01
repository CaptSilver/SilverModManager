using SilverModManager.App.UI.Controls.Cards;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Networking.Thunderstore;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.App.UI.Pages.Thunderstore;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// Lists the Thunderstore packages in the library and in a loadout, one row per package with a child row for
/// each downloaded version. Only packages destined for the game are shown, so a game whose mods don't come from
/// Thunderstore has none.
/// </summary>
[UsedImplicitly]
internal class ThunderstoreDataProvider : ALibraryDataProvider<ThunderstoreLibraryFile.ReadOnly>, ILoadoutDataProvider
{
    private readonly IConnection _connection;
    private readonly GameNames _gameNames;
    private readonly ModSourceResolver _sources;
    private readonly Lazy<IThumbnailLoader> _iconLoader;

    public ThunderstoreDataProvider(IServiceProvider serviceProvider) : this(serviceProvider, iconLoader: null) { }

    // Tests pass a loader over a fake pipeline, so no row ever reaches the network.
    internal ThunderstoreDataProvider(IServiceProvider serviceProvider, IThumbnailLoader? iconLoader)
        : base(serviceProvider.GetRequiredService<IConnection>())
    {
        _iconLoader = new Lazy<IThumbnailLoader>(() => iconLoader ?? serviceProvider.GetRequiredService<IThumbnailLoader>());
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _gameNames = new GameNames(serviceProvider.GetServices<IGameData>());
        _sources = serviceProvider.GetRequiredService<ModSourceResolver>();
    }

    private static string PackageName(ThunderstoreLibraryFile.ReadOnly file) => $"{file.Namespace}-{file.Name}";

    private static string RowName(ThunderstoreLibraryFile.ReadOnly file)
    {
        return ThunderstoreRowText.RowName(file.Namespace, file.Name, file.VersionNumber, file.AsLibraryFile().AsLibraryItem().Name);
    }

    // Name, owner, icon and source of a package's parent row. The grouping key stays "Namespace-Name"; this is
    // only what the row shows.
    private void AddPackageIdentity(CompositeItemModel<EntityId> parentItemModel, IEnumerable<ThunderstoreLibraryFile.ReadOnly> files)
    {
        var first = files.First();
        parentItemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: ThunderstoreRowText.RowName(first.Namespace, first.Name, first.VersionNumber, libraryItemName: "")));
        parentItemModel.Add(SharedColumns.Name.SubtitleComponentKey, new StringComponent(value: $"by {first.Namespace}"));
        parentItemModel.Add(SharedColumns.Name.ImageComponentKey, IconOf(ThunderstoreIcons.UrlFor(first.Namespace, first.Name, HighestVersion(files))));
        SharedColumns.Source.Add(parentItemModel, ModSources.Thunderstore);
    }

    // Through the Thunderstore page's loader rather than the bare pipeline: the pipeline has no cache, and a row
    // fetches its icon again each time it scrolls into view. The loader also turns a failed fetch into its placeholder.
    private ImageComponent IconOf(Uri url)
    {
        var loader = _iconLoader.Value;
        return new ImageComponent(
            initialValue: AsBitmap(loader.TryGetCached(url)),
            valueObservable: R3.Observable.FromAsync(async cancellationToken => AsBitmap(await loader.LoadAsync(url, cancellationToken)), configureAwait: false)
        );
    }

    private static Bitmap AsBitmap(IImage? image) => image as Bitmap ?? ImagePipelines.ModPageThumbnailFallback;

    protected override Optional<LibraryFile.ReadOnly> AsLibraryFile(ThunderstoreLibraryFile.ReadOnly model) => model.AsLibraryFile();

    // Community stays on the file as display metadata and as the lock and update key; which game a package is listed
    // under comes from its destination.
    private static IObservable<IChangeSet<IGroup<ThunderstoreLibraryFile.ReadOnly, EntityId, string>, string>> ByPackage(
        IObservable<IChangeSet<ThunderstoreLibraryFile.ReadOnly, EntityId>> files)
        => files.Group(PackageName);

    // A package has no entity of its own, so its row is keyed by an id derived from the package name. Keying it
    // by one of its files would leave the row holding the id of a deleted entity once that version goes. The id
    // lives in the temporary partition, which no stored entity can occupy, so it can't collide with a file's id.
    internal static EntityId ParentKey(string packageName)
    {
        // FNV-1a rather than string.GetHashCode, which is randomized per process.
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(packageName))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return PartitionId.Temp.MakeEntityId(hash & 0x00FF_FFFF_FFFF_FFFFUL);
    }

    private static EntityId ParentKey(IGroup<ThunderstoreLibraryFile.ReadOnly, EntityId, string> package) => ParentKey(package.Key);

    private static string HighestVersion(IEnumerable<ThunderstoreLibraryFile.ReadOnly> files)
    {
        var highest = files
            .Select(static file => (file.VersionNumber, Parsed: ThunderstoreVersionNumber.TryParse(file.VersionNumber, out var number) ? number : default))
            .OptionalMaxBy(static version => version.Parsed);
        return highest.HasValue ? highest.Value.VersionNumber : string.Empty;
    }

    private IObservable<Optional<ModUpdate>> ObserveUpdate(LibraryItem.ReadOnly item)
        => _sources.For(item)?.ObserveUpdate(item) ?? System.Reactive.Linq.Observable.Return(Optional<ModUpdate>.None);

    // The update for the package as a whole. The source is asked again whenever the package gains or loses a
    // downloaded version, because the versions are what it looks the update up for.
    private IObservable<Optional<ModUpdate>> ObservePackageUpdate(IObservable<IChangeSet<ThunderstoreLibraryFile.ReadOnly, EntityId>> files)
    {
        return files
            .QueryWhenChanged(static query => query.Items.Select(static file => file.AsLibraryFile().AsLibraryItem()).ToArray())
            .Select(items => items.Length > 0 && _sources.For(items[0]) is { } source
                ? source.ObserveUpdate(items)
                : System.Reactive.Linq.Observable.Return(Optional<ModUpdate>.None))
            .Switch();
    }

    public override IObservable<IChangeSet<EntityId, EntityId>> ObserveModsTabItems(GameId game)
        => ObserveOwnItems(game).Transform(static (_, id) => id);

    public override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter)
    {
        return ByPackage(ObserveOwnItems(libraryFilter))
            .Transform(package => ToLibraryItemModel(package, libraryFilter))
            .ChangeKey(static model => model.Key);
    }

    private CompositeItemModel<EntityId> ToLibraryItemModel(IGroup<ThunderstoreLibraryFile.ReadOnly, EntityId, string> package, LibraryFilter libraryFilter)
    {
        var files = package.Cache.Connect().RefCount();

        var linkedLoadoutItemsObservable = files.MergeManyChangeSets(file => LibraryDataProviderHelper.GetLinkedLoadoutItems(_connection, libraryFilter, file.Id));

        var parentItemModel = new CompositeItemModel<EntityId>(ParentKey(package))
        {
            HasChildrenObservable = files.IsNotEmpty(),
            ChildrenObservable = files.Transform(file => ToLibraryItemModel(file, libraryFilter)),
        };

        AddPackageIdentity(parentItemModel, package.Cache.Items);

        var sizeObservable = files
            .TransformImmutable(static file => file.AsLibraryFile().Size)
            .ForAggregation()
            .Sum(static size => (long)size.Value)
            .Select(static size => Size.FromLong(size));
        parentItemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(initialValue: Size.Zero, valueObservable: sizeObservable));

        var downloadedDateObservable = files
            .TransformImmutable(static file => file.GetCreatedAt())
            .QueryWhenChanged(static query => query.Items.OptionalMaxBy(static date => date).ValueOr(DateTimeOffset.MinValue));
        parentItemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(
            initialValue: package.Cache.Items.Min(static file => file.GetCreatedAt()),
            valueObservable: downloadedDateObservable
        ));

        var highestVersionObservable = files.QueryWhenChanged(static query => HighestVersion(query.Items));
        var initialVersion = HighestVersion(package.Cache.Items);
        parentItemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(initialVersion, highestVersionObservable));

        // The newest of the updates available for any of the package's versions.
        var packageUpdate = ObservePackageUpdate(files);
        var currentUpdateVersionObservable = packageUpdate
            .Select(static optional => optional.HasValue ? optional.Value.CurrentVersion : "")
            .OnUI();
        AddNewVersionComponent(
            parentItemModel,
            () => new VersionComponent(initialValue: string.Empty, valueObservable: currentUpdateVersionObservable),
            packageUpdate);

        LibraryDataProviderHelper.AddRelatedCollectionsComponent(parentItemModel, linkedLoadoutItemsObservable);

        var matchesObservable = files
            .TransformOnObservable(file => LibraryDataProviderHelper.GetLinkedLoadoutItems(_connection, libraryFilter, file.Id).IsNotEmpty())
            .QueryWhenChanged(static query =>
            {
                var (numInstalled, numTotal) = (0, 0);
                foreach (var isInstalled in query.Items)
                {
                    numInstalled += isInstalled ? 1 : 0;
                    numTotal++;
                }

                return new MatchesData(numInstalled, numTotal);
            });

        parentItemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(files.TransformImmutable(static file => file.AsLibraryFile().AsLibraryItem().LibraryItemId)));
        LibraryDataProviderHelper.AddInstalledDateComponent(parentItemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddInstallActionComponent(parentItemModel, matchesObservable);
        AddPageActions(parentItemModel);
        LibraryDataProviderHelper.AddDeleteItemActionComponent(parentItemModel);
        LibraryDataProviderHelper.AddDestinationComponents(parentItemModel, _connection, libraryFilter.Game.GameId, _gameNames);

        return parentItemModel;
    }

    private CompositeItemModel<EntityId> ToLibraryItemModel(ThunderstoreLibraryFile.ReadOnly file, LibraryFilter libraryFilter)
    {
        var libraryFile = file.AsLibraryFile();
        var linkedLoadoutItemsObservable = LibraryDataProviderHelper
            .GetLinkedLoadoutItems(_connection, libraryFilter, file.Id)
            .RefCount();

        var itemModel = new CompositeItemModel<EntityId>(file.Id);
        itemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(libraryFile.AsLibraryItem()));
        itemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: RowName(file)));
        SharedColumns.Source.Add(itemModel, ModSources.Thunderstore);
        itemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(value: file.GetCreatedAt()));
        itemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(value: file.VersionNumber));
        itemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(value: libraryFile.Size));

        AddNewVersionComponent(itemModel, () => new VersionComponent(value: file.VersionNumber), ObserveUpdate(libraryFile.AsLibraryItem()));
        LibraryDataProviderHelper.AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable);

        LibraryDataProviderHelper.AddInstalledDateComponent(itemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddInstallActionComponent(itemModel, linkedLoadoutItemsObservable);
        AddPageActions(itemModel);
        LibraryDataProviderHelper.AddDeleteItemActionComponent(itemModel);
        LibraryDataProviderHelper.AddDestinationComponents(itemModel, _connection, libraryFilter.Game.GameId, _gameNames);

        return itemModel;
    }

    // The page and changelog buttons open the package's pages on thunderstore.io; there is no hide switch yet.
    private static void AddPageActions(CompositeItemModel<EntityId> itemModel)
    {
        LibraryDataProviderHelper.AddViewChangelogActionComponent(itemModel);
        LibraryDataProviderHelper.AddViewModPageActionComponent(itemModel);
        LibraryDataProviderHelper.AddHideUpdatesActionComponent(itemModel, isEnabled: false, isVisible: false);
    }

    // The new version shows next to the installed one, and the Update button applies it through the source.
    private static void AddNewVersionComponent(
        CompositeItemModel<EntityId> itemModel,
        Func<VersionComponent> currentVersion,
        IObservable<Optional<ModUpdate>> update)
    {
        // The checker refreshes on whichever thread asked for the check, and the components belong to the UI thread.
        var updateOnUi = update.OnUI();
        var newestVersionObservable = updateOnUi.Select(static optional => optional.Convert(static found => found.NewestVersion));

        itemModel.AddObservable(
            key: LibraryColumns.ItemVersion.NewVersionComponentKey,
            observable: newestVersionObservable,
            componentFactory: (valueObservable, initialValue) => new LibraryComponents.NewVersionAvailable(
                currentVersion: currentVersion(),
                newVersion: initialValue,
                newVersionObservable: valueObservable
            )
        );

        itemModel.AddObservable(
            key: LibraryColumns.Actions.UpdateComponentKey,
            observable: updateOnUi,
            componentFactory: static (valueObservable, initialValue) => new LibraryComponents.UpdateAction(
                initialValue,
                valueObservable
            )
        );
    }

    private IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> ObserveLinkedLoadoutItems(
        IGroup<ThunderstoreLibraryFile.ReadOnly, EntityId, string> package,
        LoadoutFilter loadoutFilter)
    {
        return package.Cache
            .Connect()
            .FilterOnObservable((_, fileId) => _connection.ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItemId, fileId).IsNotEmpty())
            // NOTE: DynamicData 9.0.4 uses ReferenceEquals for value types, so the datoms need a comparer that looks at the entity.
            .MergeManyChangeSets((_, fileId) => _connection.ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItemId, fileId).AsEntityIds(), equalityComparer: DatomEntityIdEqualityComparer.Instance)
            .FilterInStaticLoadout(_connection, loadoutFilter)
            .Transform(datom => LoadoutItem.Load(_connection.Db, datom.E));
    }

    private IObservable<IChangeSet<IGroup<ThunderstoreLibraryFile.ReadOnly, EntityId, string>, string>> FilterLoadoutPackages(LoadoutFilter loadoutFilter)
    {
        // The game comes from the installation's recorded id, not the game registry: the registry throws for an
        // installation it hasn't located, and a loadout's page shouldn't fail because its game went missing.
        return ByPackage(ObserveOwnItemsForLoadout(loadoutFilter.LoadoutId))
            .FilterOnObservable(package => ObserveLinkedLoadoutItems(package, loadoutFilter).IsNotEmpty());
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutPackages(loadoutFilter)
            .Transform(package => ToLoadoutItemModel(package, loadoutFilter))
            .ChangeKey(static model => model.Key);
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutPackages(loadoutFilter).QueryWhenChanged(static query => query.Count).Prepend(0);
    }

    private CompositeItemModel<EntityId> ToLoadoutItemModel(IGroup<ThunderstoreLibraryFile.ReadOnly, EntityId, string> package, LoadoutFilter loadoutFilter)
    {
        var linkedItemsObservable = ObserveLinkedLoadoutItems(package, loadoutFilter).RefCount();

        var parentItemModel = new CompositeItemModel<EntityId>(ParentKey(package))
        {
            HasChildrenObservable = linkedItemsObservable.IsNotEmpty(),
            ChildrenObservable = linkedItemsObservable.Transform(loadoutItem => ToChildLoadoutItemModel(_connection, loadoutItem)),
        };

        AddPackageIdentity(parentItemModel, package.Cache.Items);

        LoadoutDataProviderHelper.AddDateComponent(parentItemModel, package.Cache.Items.Min(static file => file.GetCreatedAt()), linkedItemsObservable);
        LoadoutDataProviderHelper.AddCollections(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddParentCollectionsDisabled(_connection, parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddMixLockedAndParentDisabled(_connection, parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddLockedEnabledStates(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddEnabledStateToggle(_connection, parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddLoadoutItemIds(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(parentItemModel);
        LoadoutDataProviderHelper.AddViewModFilesActionComponent(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddUninstallItemComponent(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddAllowProtectedPathsAction(_connection, parentItemModel, linkedItemsObservable);

        return parentItemModel;
    }

    private static CompositeItemModel<EntityId> ToChildLoadoutItemModel(IConnection connection, LoadoutItem.ReadOnly loadoutItem)
    {
        var childModel = LoadoutDataProviderHelper.ToChildItemModel(connection, loadoutItem);
        SharedColumns.Source.Add(childModel, ModSources.Thunderstore);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(childModel);
        return childModel;
    }
}
