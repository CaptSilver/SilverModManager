using System.Reactive.Linq;
using DynamicData;
using DynamicData.Aggregation;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.ValueSerializers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;
using UIObservableExtensions = SilverModManager.App.UI.Extensions.ObservableExtensions;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// The rows of a library file that is neither a Nexus page's file nor an archive of the app's own: a file the user
/// added or one a download left behind. A subclass says which model it owns, which of those files it lists, and
/// where each one came from; the columns, the install state and the loadout rows are the same for all of them.
/// </summary>
/// <typeparam name="TModel">The generated <c>ReadOnly</c> struct of the model the provider lists.</typeparam>
internal abstract class ALibraryFileDataProvider<TModel> : ALibraryDataProvider<TModel>, ILoadoutDataProvider
    where TModel : struct, IReadOnlyModel<TModel>
{
    private readonly IConnection _connection;
    private readonly GameNames _gameNames;
    private readonly ILogger _logger;

    protected ALibraryFileDataProvider(IConnection connection, GameNames gameNames, ILogger logger) : base(connection)
    {
        _logger = logger;
        _connection = connection;
        _gameNames = gameNames;
    }

    /// <summary>The file the model's row stands for.</summary>
    protected abstract LibraryFile.ReadOnly FileOf(TModel model);

    /// <summary>The Source column of a file that no Wabbajack list fetched.</summary>
    protected abstract string SourceOf(TModel model);

    protected override Optional<LibraryFile.ReadOnly> AsLibraryFile(TModel model) => FileOf(model);

    public override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter)
    {
        return ObserveOwnItems(libraryFilter).Transform(model => ToLibraryItemModel(libraryFilter, model));
    }

    public override IObservable<IChangeSet<EntityId, EntityId>> ObserveModsTabItems(GameId game)
        => ObserveOwnItems(game).Transform(static (_, id) => id);

    private CompositeItemModel<EntityId> ToLibraryItemModel(LibraryFilter libraryFilter, TModel model)
    {
        var linkedLoadoutItemsObservable = LibraryDataProviderHelper
            .GetLinkedLoadoutItems(_connection, libraryFilter, model.Id)
            .RefCount();

        var childrenObservable = UIObservableExtensions.ReturnFactory(() =>
        {
            var itemModel = new CompositeItemModel<EntityId>(model.Id);
            SetupLibraryItemModel(itemModel, model, libraryFilter, linkedLoadoutItemsObservable);

            return new ChangeSet<CompositeItemModel<EntityId>, EntityId>([
                new Change<CompositeItemModel<EntityId>, EntityId>(
                    reason: ChangeReason.Add,
                    key: model.Id,
                    current: itemModel
                )]
            );
        });
        
        var hasChildrenObservable = childrenObservable.IsNotEmpty();

        var parentItemModel = new CompositeItemModel<EntityId>(model.Id)
        {
            HasChildrenObservable = hasChildrenObservable,
            ChildrenObservable = childrenObservable,
        };

        SetupLibraryItemModel(parentItemModel, model, libraryFilter, linkedLoadoutItemsObservable);

        parentItemModel.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));
        return parentItemModel;
    }

    private void SetupLibraryItemModel(
        CompositeItemModel<EntityId> itemModel,
        TModel model,
        LibraryFilter libraryFilter,
        IObservable<IChangeSet<LoadoutItem.ReadOnly, EntityId>> linkedLoadoutItemsObservable)
    {
        itemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: FileOf(model).AsLibraryItem().Name));
        // A file the app fetched for a Wabbajack modlist came from that list, not from the user's disk.
        var db = _connection.Db;
        var fetchedForModlist = LibraryDataProviderHelper.IsFetchedForModlist(db, model.Id);
        _logger.LogTrace("Source of library file {FileId}: fetchedForModlist={FetchedForModlist}, db basis tx {BasisTx}", model.Id, fetchedForModlist, db.BasisTxId);
        SharedColumns.Source.Add(itemModel,
            fetchedForModlist ? ModSources.Wabbajack : SourceOf(model),
            LibraryDataProviderHelper.ObserveFetchedForModlist(_connection, model.Id).Select(fetched => fetched ? ModSources.Wabbajack : SourceOf(model)));
        itemModel.Add(LibraryColumns.DownloadedDate.ComponentKey, new DateComponent(value: FileOf(model).AsLibraryItem().GetCreatedAt()));
        itemModel.Add(SharedColumns.ItemSize.ComponentKey, new SizeComponent(value: FileOf(model).Size));
        itemModel.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(FileOf(model).AsLibraryItem()));

        LibraryDataProviderHelper.AddInstalledDateComponent(itemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddInstallActionComponent(itemModel, linkedLoadoutItemsObservable);
        LibraryDataProviderHelper.AddViewChangelogActionComponent(itemModel, isEnabled: false);
        LibraryDataProviderHelper.AddViewModPageActionComponent(itemModel, isEnabled: false);
        LibraryDataProviderHelper.AddHideUpdatesActionComponent(itemModel, isEnabled: false, isVisible: false);
        LibraryDataProviderHelper.AddDestinationComponents(itemModel, _connection, libraryFilter.Game.GameId, _gameNames);
        
        // Get related collections
        var md5 = FileOf(model).Md5;
        if (!md5.HasValue)
        {
            LibraryDataProviderHelper.AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable);
        }
        else
        {
            var conn = _connection;

            var relatedDownloadedCollectionsObservable = conn
                .ObserveDatoms(CollectionDownloadExternal.Md5)
                .FilterImmutable(datom => Md5Value.From(UInt128Serializer.Read(datom.ValueSpan)) == md5.Value)
                .AsEntityIds()
                .Distinct()
                .Transform(datom => SilverModManager.Abstractions.NexusModsLibrary.Models.CollectionDownload.Load(conn.Db, datom.E).CollectionRevision.Collection)
                .ChangeKey(collection => collection.Id);
            
            LibraryDataProviderHelper.AddRelatedCollectionsComponent(itemModel, linkedLoadoutItemsObservable, relatedDownloadedCollectionsObservable );
        }
        
    }

    private IObservable<IChangeSet<TModel, EntityId>> FilterLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return ObserveOwnItemsForLoadout(loadoutFilter.LoadoutId)
            .FilterOnObservable((_, entityId) => _connection
                .ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItemId, entityId)
                .AsEntityIds()
                .FilterInStaticLoadout(_connection, loadoutFilter)
                .IsNotEmpty()
            );
    }

    public IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).Transform(model => ToLoadoutItemModel(loadoutFilter, model));
    }

    public IObservable<int> CountLoadoutItems(LoadoutFilter loadoutFilter)
    {
        return FilterLoadoutItems(loadoutFilter).QueryWhenChanged(static query => query.Count).Prepend(0);
    }

    private CompositeItemModel<EntityId> ToLoadoutItemModel(LoadoutFilter loadoutFilter, TModel model)
    {
        var linkedItemsObservable = _connection.ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItem, model.Id)
            .AsEntityIds()
            .FilterInStaticLoadout(_connection, loadoutFilter)
            .Transform(datom => LoadoutItem.Load(_connection.Db, datom.E))
            .RefCount();

        var hasChildrenObservable = linkedItemsObservable.IsNotEmpty();
        var childrenObservable = linkedItemsObservable.Transform(loadoutItem => ToChildLoadoutItemModel(_connection, loadoutItem, SourceOf(model)));

        var parentItemModel = new CompositeItemModel<EntityId>(model.Id)
        {
            HasChildrenObservable = hasChildrenObservable,
            ChildrenObservable = childrenObservable,
        };

        parentItemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: FileOf(model).AsLibraryItem().Name));
        SharedColumns.Source.Add(parentItemModel, SourceOf(model));
        parentItemModel.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));

        LoadoutDataProviderHelper.AddDateComponent(parentItemModel, FileOf(model).AsLibraryItem().GetCreatedAt(), linkedItemsObservable);
        LoadoutDataProviderHelper.AddCollections(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddParentCollectionsDisabled(_connection, parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddMixLockedAndParentDisabled(_connection, parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddLockedEnabledStates(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddEnabledStateToggle(_connection, parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddLoadoutItemIds(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(parentItemModel, isEnabled: false);
        LoadoutDataProviderHelper.AddViewModFilesActionComponent(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddUninstallItemComponent(parentItemModel, linkedItemsObservable);
        LoadoutDataProviderHelper.AddAllowProtectedPathsAction(_connection, parentItemModel, linkedItemsObservable);

        return parentItemModel;
    }
    
    private static CompositeItemModel<EntityId> ToChildLoadoutItemModel(IConnection connection, LoadoutItem.ReadOnly loadoutItem, string source)
    {
        var childModel = LoadoutDataProviderHelper.ToChildItemModel(connection, loadoutItem);
        SharedColumns.Source.Add(childModel, source);
        LoadoutDataProviderHelper.AddViewModPageActionComponent(childModel, isEnabled: false);
        return childModel;
    }
}
