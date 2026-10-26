using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.Merge;
using NexusMods.HyperDuck;
using SilverModManager.MnemonicDB.Abstractions;
using Humanizer;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Resources;
using SilverModManager.UI.Sdk;
using R3;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Sorting;

public class FileConflictsViewModel : AViewModel<IFileConflictsViewModel>, IFileConflictsViewModel
{
    public FileConflictsTreeDataGridAdapter TreeDataGridAdapter { get; }

    private readonly BindableReactiveProperty<ListSortDirection> _sortDirectionCurrent;
    private readonly ILoadoutManager _loadoutManager;
    private readonly LoadoutId _loadoutId;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWindowManager _windowManager;
    private readonly ILogger<FileConflictsViewModel> _logger;
    private readonly IMergeIndexSource? _mergeSource;
    public IReadOnlyBindableReactiveProperty<ListSortDirection> SortDirectionCurrent => _sortDirectionCurrent;
    public IReadOnlyBindableReactiveProperty<bool> IsAscending { get; }
    public IReadOnlyBindableReactiveProperty<string> OrderHint { get; }

    public R3.ReactiveCommand SwitchSortDirectionCommand { get; }

    public FileConflictsViewModel(IServiceProvider serviceProvider, IWindowManager windowManager, LoadoutId loadoutId)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();

        var loadout = Loadout.Load(connection.Db, loadoutId);
        Debug.Assert(loadout.IsValid());

        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _loadoutId = loadoutId;
        _serviceProvider = serviceProvider;
        _windowManager = windowManager;
        _logger = serviceProvider.GetRequiredService<ILogger<FileConflictsViewModel>>();

        _mergeSource = serviceProvider.GetService<IMergeIndexSource>();

        _sortDirectionCurrent = new BindableReactiveProperty<ListSortDirection>(ListSortDirection.Ascending);
        var isLoadOrder = _mergeSource?.ProvidesIndex(loadout) == true;
        OrderHint = _sortDirectionCurrent.Select(direction => OrderHintFor(isLoadOrder, direction)).ToBindableReactiveProperty(OrderHintFor(isLoadOrder, ListSortDirection.Ascending));
        IsAscending = _sortDirectionCurrent.Select(direction => direction == ListSortDirection.Ascending).ToBindableReactiveProperty();

        SwitchSortDirectionCommand = new R3.ReactiveCommand(_ =>
        {
            var newDirection = SortDirectionCurrent.Value == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            _sortDirectionCurrent.Value = newDirection;
        });
        
        TreeDataGridAdapter = new FileConflictsTreeDataGridAdapter(serviceProvider, connection, synchronizer, SortDirectionCurrent.AsObservable(), loadoutId);

        this.WhenActivated(disposables =>
        {
            TreeDataGridAdapter.Activate().AddTo(disposables);

            TreeDataGridAdapter.MessageSubject.SubscribeAwait(async (msg, cancellationToken) =>
            {
                await msg.Match<Task>(
                    ViewConflicts,
                    OpenRules,
                    moveUp => Move(moveUp.Item.Key, moveUp.Item.Get<FileConflictsComponents.NeighbourIds>(FileConflictsColumns.IndexColumn.NeighbourIdsComponentKey), _sortDirectionCurrent.Value, moveUp: true), 
                    moveDown => Move(moveDown.Item.Key, moveDown.Item.Get<FileConflictsComponents.NeighbourIds>(FileConflictsColumns.IndexColumn.NeighbourIdsComponentKey), _sortDirectionCurrent.Value, moveUp: false)
                );
            }).AddTo(disposables);

            // Update drop position indicator
            TreeDataGridAdapter.RowDragOverSubject.Subscribe(dragDropPayload =>
            {
                var (sourceModels, targetModel, eventArgs) = dragDropPayload;

                if (eventArgs.Position != TreeDataGridRowDropPosition.Inside) return;
                    
                // Update the drop position for the inside case to be before or after
                eventArgs.Position = PointerIsInVerticalTopHalf(eventArgs) ? TreeDataGridRowDropPosition.Before : TreeDataGridRowDropPosition.After;
            }).AddTo(disposables);

            // Handle row drops
            TreeDataGridAdapter.RowDropSubject.SubscribeAwait(async (eventPayload, _) =>
            {
                var (itemModels, targetModel, _, _, eventArgs) = eventPayload;

                var visuallyAbove = eventArgs.Position switch
                {
                    TreeDataGridRowDropPosition.Before => true,
                    TreeDataGridRowDropPosition.After => false,
                    _ => PointerIsInVerticalTopHalf(eventArgs),
                };

                await Drop(itemModels.Select(x => x.Key).ToArray(), targetModel.Key, visuallyAbove);
            },
            awaitOperation: AwaitOperation.Drop).AddTo(disposables);
        });
    }

    /// <summary>Says which end of the list wins; me3 games call the order a load order because me3 mounts the last package.</summary>
    internal static string OrderHintFor(bool isLoadOrder, ListSortDirection direction) => (isLoadOrder, direction) switch
    {
        (true, ListSortDirection.Ascending) => Language.FileConflicts_Order_LoadAscending,
        (true, _) => Language.FileConflicts_Order_LoadDescending,
        (false, ListSortDirection.Ascending) => Language.FileConflicts_Order_PriorityAscending,
        (false, _) => Language.FileConflicts_Order_PriorityDescending,
    };

    /// <summary>
    /// Moves the dragged rows next to the row or collection they were dropped on. A collection stands for all of its
    /// mods, which move together and keep their order; a mod dragged out of one on its own leaves the rest behind.
    /// </summary>
    /// <param name="movedKeys">The keys of the dragged rows: a mod's priority row, or a collection.</param>
    /// <param name="target">The key of the row dropped on.</param>
    /// <param name="visuallyAbove">Whether the drop is above the target as the list is drawn.</param>
    internal async Task Drop(EntityId[] movedKeys, EntityId target, bool visuallyAbove)
    {
        var rows = PriorityUnits.Load(_serviceProvider.GetRequiredService<IConnection>().Db, _loadoutId);
        var moved = PriorityUnits.Expand(rows, movedKeys);
        if (moved.Length == 0) return;

        var aboveInPriority = PriorityUnits.IsAboveInPriority(_sortDirectionCurrent.Value, visuallyAbove);
        if (!PriorityUnits.TryFindLoser(rows, moved, target, aboveInPriority, out var loser))
        {
            _logger.LogWarning("Ignored a drop on {Target}: it is not in the priority order of loadout {Loadout}", target, _loadoutId);
            return;
        }

        await RunMove(() => loser is { } loserId
            ? _loadoutManager.ResolveFileConflicts(moved, loserId)
            : _loadoutManager.LoseAllFileConflicts(moved));
    }

    internal async Task Move(EntityId toMove, FileConflictsComponents.NeighbourIds neighbourIds, ListSortDirection sortDirection, bool moveUp)
    {
        var (winnerId, loserId) = (sortDirection, moveUp) switch
        {
            (ListSortDirection.Ascending, moveUp: false) => (toMove, neighbourIds.Next.Value),
            (ListSortDirection.Ascending, moveUp: true) => (neighbourIds.Prev.Value, toMove),
            (ListSortDirection.Descending, moveUp: false) => (neighbourIds.Prev.Value, toMove),
            (ListSortDirection.Descending, moveUp: true) => (toMove, neighbourIds.Next.Value),
        };

        var rows = PriorityUnits.Load(_serviceProvider.GetRequiredService<IConnection>().Db, _loadoutId);

        // The neighbours of a collection row are the rows beside its mods. What it swaps places with is the whole
        // collection when that row is a mod of one, not the single mod.
        if (PriorityUnits.IsCollection(rows, toMove))
        {
            if (winnerId == toMove) loserId = PriorityUnits.UnitOf(rows, loserId);
            else winnerId = PriorityUnits.UnitOf(rows, winnerId);
        }

        var winners = PriorityUnits.Expand(rows, [winnerId]);
        var loser = PriorityUnits.TopRow(rows, loserId);
        await RunMove(() => _loadoutManager.ResolveFileConflicts(winnerIds: winners, loser));
    }

    /// <summary>
    /// Runs a priority move. One that a group rule stops is not applied: the player either cancels, or has the rule
    /// removed and the move repeated. Every way of moving a mod goes through here.
    /// </summary>
    internal async Task RunMove(Func<ValueTask<ConflictMoveResult>> move)
    {
        var result = await move();

        // Removing the rule can expose another one the move also breaks, so the question is asked again for it.
        while (result.IsRuleViolation)
        {
            var rule = result.Rule;
            if (!await AskToRemoveRule(rule)) return;

            await _loadoutManager.RemoveGroupRule(rule.LoadoutGroupRuleId);
            result = await move();
        }
    }

    private async Task<bool> AskToRemoveRule(LoadoutGroupRule.ReadOnly rule)
    {
        var source = rule.Source.AsLoadoutItem().Name;
        var other = rule.Other.AsLoadoutItem().Name;
        _logger.LogInformation("Refused a priority move: it would break the rule that {Source} must {Kind} {Other}", source, rule.Kind, other);

        var winner = rule.Kind == GroupRuleKind.After ? source : other;
        var loser = rule.Kind == GroupRuleKind.After ? other : source;
        var markdownRendererViewModel = _serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdownRendererViewModel.Contents = string.Format(Language.FileConflicts_Refused_Message, winner, loser);

        var dialog = DialogFactory.CreateStandardDialog(
            title: Language.FileConflicts_Refused_Title,
            new StandardDialogParameters { Markdown = markdownRendererViewModel },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(Language.FileConflicts_Refused_RemoveAndMove, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
            ]);

        var answer = await _windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return answer.ButtonId == ButtonDefinitionId.Accept;
    }

    /// <summary>Opens the Rules editor for the mod or collection, where its Before/After rules are added and deleted.</summary>
    internal async Task OpenRules(FileConflictsTreeDataGridAdapter.RulesMessage msg)
    {
        var connection = _serviceProvider.GetRequiredService<IConnection>();
        var db = connection.Db;

        // A collection row is keyed by the collection's own group; a mod row by its priority row.
        var group = CollectionGroup.Load(db, msg.Key).IsValid()
            ? LoadoutItemGroupId.From(msg.Key)
            : LoadoutItemGroupPriority.Load(db, LoadoutItemGroupPriorityId.From(msg.Key)).TargetId;

        var content = new FileConflictRulesViewModel(connection, _loadoutManager, _loadoutId, group);
        var title = string.Format(Language.FileConflicts_Rules_Title, LoadoutItem.Load(db, group).Name);
        _ = await _windowManager.ShowDialog(
            DialogFactory.CreateDialog(title, [DialogStandardButtons.Close], content, DialogWindowSize.Large),
            DialogWindowType.Modeless);
    }

    /// <summary>Opens the per-path list for the mod, where a path's winner can be pinned.</summary>
    internal async Task ViewConflicts(FileConflictsTreeDataGridAdapter.ViewConflictsMessage msg)
    {
        var connection = _serviceProvider.GetRequiredService<IConnection>();
        var priority = LoadoutItemGroupPriority.Load(connection.Db, msg.PriorityId);

        var content = new FileConflictPathsViewModel(
            _loadoutManager,
            _loadoutId,
            priority.TargetId,
            () =>
            {
                var merges = _mergeSource?.IndexOf(Loadout.Load(connection.Db, _loadoutId));
                return FileConflictPaths.Load(connection, _loadoutId, msg.PriorityId, merges);
            });

        var title = string.Format(Language.FileConflicts_Paths_Title, priority.Target.AsLoadoutItem().Name);
        _ = await _windowManager.ShowDialog(
            DialogFactory.CreateDialog(title, [DialogStandardButtons.Close], content, DialogWindowSize.Large),
            DialogWindowType.Modeless);
    }

    private static bool PointerIsInVerticalTopHalf(TreeDataGridRowDragEventArgs eventArgs)
    {
        var positionY = eventArgs.Inner.GetPosition(eventArgs.TargetRow).Y / eventArgs.TargetRow.Bounds.Height;
        return positionY < 0.5;
    }
}

public class FileConflictsTreeDataGridAdapter : TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>,
    ITreeDataGirdMessageAdapter<OneOf.OneOf<
        FileConflictsTreeDataGridAdapter.ViewConflictsMessage,
        FileConflictsTreeDataGridAdapter.RulesMessage,
        FileConflictsTreeDataGridAdapter.MoveUpCommandPayload,
        FileConflictsTreeDataGridAdapter.MoveDownCommandPayload
    >>
{
    public record ViewConflictsMessage(LoadoutItemGroupPriorityId PriorityId);
    public record RulesMessage(EntityId Key)
    {
        public RulesMessage(LoadoutItemGroupPriorityId priorityId) : this(priorityId.Value) { }
    }
    public readonly record struct MoveUpCommandPayload(CompositeItemModel<EntityId> Item);
    public readonly record struct MoveDownCommandPayload(CompositeItemModel<EntityId> Item);

    private readonly IConnection _connection;
    private readonly ILoadoutSynchronizer _synchronizer;
    private readonly IResourceLoader<EntityId, Bitmap> _modPageThumbnailPipeline;
    private readonly LoadoutId _loadoutId;
    private readonly IMergeIndexSource? _mergeSource;
    private readonly IDisposable _activationDisposable;
    private readonly object _sharedFileCountsLock = new();
    private (TxId Basis, Dictionary<LoadoutItemGroupId, (long Winning, long Losing)> Counts)? _sharedFileCounts;

    private readonly Observable<ListSortDirection> _sortDirectionObservable;
    public Subject<OneOf.OneOf<ViewConflictsMessage, RulesMessage, MoveUpCommandPayload, MoveDownCommandPayload>> MessageSubject { get; } = new();

    public FileConflictsTreeDataGridAdapter(
        IServiceProvider serviceProvider, 
        IConnection connection, 
        ILoadoutSynchronizer synchronizer, 
        Observable<ListSortDirection> sortDirectionObservable,
        LoadoutId loadoutId) : base(serviceProvider)
    {
        _connection = connection;
        _synchronizer = synchronizer;
        _loadoutId = loadoutId;
        _mergeSource = serviceProvider.GetService<IMergeIndexSource>();
        _modPageThumbnailPipeline = ImagePipelines.GetModPageThumbnailPipeline(serviceProvider);
        
        var ascendingComparer = OrderComparer(ListSortDirection.Ascending);
        var descendingComparer = OrderComparer(ListSortDirection.Descending);

        _sortDirectionObservable = sortDirectionObservable;
        var comparerObservable = sortDirectionObservable
            .Select(direction => direction == ListSortDirection.Ascending ? ascendingComparer : descendingComparer)
            .DistinctUntilChanged();

        _activationDisposable = this.WhenActivated( (self, disposables)  =>
        {
            comparerObservable
                .Subscribe(comparer => CustomSortComparer.Value = comparer)
                .AddTo(disposables);
        });
    }

    protected override bool SortWithoutReset => true;

    protected override void BeforeModelActivationHook(CompositeItemModel<EntityId> model)
    {
        base.BeforeModelActivationHook(model);

        // View conflicts command
        model.SubscribeToComponentAndTrack<FileConflictsComponents.ViewAction, FileConflictsTreeDataGridAdapter>(
            key: FileConflictsColumns.Actions.ViewComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandViewConflicts.Subscribe((self, itemModel, component), static (_, state) =>
            {
                var (self, itemModel, component) = state;
                self.MessageSubject.OnNext(new ViewConflictsMessage(itemModel.Key));
            })
        );

        // Rules command
        model.SubscribeToComponentAndTrack<FileConflictsComponents.ViewAction, FileConflictsTreeDataGridAdapter>(
            key: FileConflictsColumns.Actions.ViewComponentKey,
            state: this,
            factory: static (self, itemModel, component) => component.CommandEditRules.Subscribe((self, itemModel), static (_, state) =>
            {
                var (self, itemModel) = state;
                self.MessageSubject.OnNext(new RulesMessage(itemModel.Key));
            })
        );

        // Move up command
        model.SubscribeToComponentAndTrack<SharedComponents.IndexComponent, FileConflictsTreeDataGridAdapter>(
            key: FileConflictsColumns.IndexColumn.IndexComponentKey,
            state: this,
            factory: static (adapter, itemModel, component) => component.MoveUp.Subscribe((adapter, itemModel, component), static (_, tuple) =>
            {
                var (adapter, itemModel, _) = tuple;
                adapter.MessageSubject.OnNext(new MoveUpCommandPayload(itemModel));
            })
        );

        // Move down command
        model.SubscribeToComponentAndTrack<SharedComponents.IndexComponent, FileConflictsTreeDataGridAdapter>(
            key: FileConflictsColumns.IndexColumn.IndexComponentKey,
            state: this,
            factory: static (adapter, itemModel, component) => component.MoveDown.Subscribe((adapter, itemModel, component), static (_, tuple) =>
            {
                var (adapter, itemModel, _) = tuple;
                adapter.MessageSubject.OnNext(new MoveDownCommandPayload(itemModel));
            })
        );
        
        // IsActive styling
        model.SubscribeToComponentAndTrack<ValueComponent<bool>, FileConflictsTreeDataGridAdapter>(
            key: FileConflictsColumns.IsActiveComponentKey,
            state: this,
            factory: static (adapter, itemModel, component) => component.Value
                .Subscribe((adapter, itemModel, component),
                    static (_, tuple) =>
                    {
                        var (_, itemModel, component) = tuple;
                        itemModel.SetStyleFlag(FileConflictsColumns.IsActiveStyleTag, component.Value.Value);
                    }
                )
        );
    }

    protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical)
    {
        // The shared-file counts are worked out before the hop to the UI thread; the first set of rows arrives on the
        // subscribing thread, so the hop off it is explicit.
        var mods = ObservePriorityGroups(_connection, _loadoutId).OffUi().Transform(AddSharedFiles, transformOnRefresh: true).OnUI().TransformWithInlineUpdate(CreateItemModel, UpdateItemModel);
        var collections = ObserveCollections(_connection, _loadoutId).OnUI().TransformWithInlineUpdate(CreateCollectionModel, UpdateCollectionModel);

        // A collection is a row of its own among the mods: moving it moves all of its mods.
        return new[] { mods, collections }.MergeChangeSets();
    }

    /// <summary>
    /// Mods go by their place in the priority order. A collection row goes with the mods it holds, at the edge that
    /// faces the top of the list, so it reads as their heading.
    /// </summary>
    internal static IComparer<CompositeItemModel<EntityId>> OrderComparer(ListSortDirection direction) => Comparer<CompositeItemModel<EntityId>>.Create((a, b) =>
        FileConflictsOrder.Compare(OrderKey(a, direction), OrderKey(b, direction), direction));

    private static FileConflictsOrder.Key OrderKey(CompositeItemModel<EntityId> model, ListSortDirection direction)
    {
        if (model.TryGet<FileConflictsComponents.UnitAnchor>(FileConflictsColumns.IndexColumn.UnitAnchorComponentKey, out var anchor))
            return FileConflictsOrder.Anchor(isCollection: true, index: 0, anchor.Lowest.Value, anchor.Highest.Value, direction);

        var index = model.Get<SharedComponents.IndexComponent>(FileConflictsColumns.IndexColumn.IndexComponentKey).SortIndex.Value;
        return FileConflictsOrder.Anchor(isCollection: false, index, lowest: 0, highest: 0, direction);
    }

    private SharedComponents.IndexComponent CreateIndexComponent(int index, string display, FileConflictsComponents.NeighbourIds neighbourIds)
    {
        var canExecuteMoveUp = _sortDirectionObservable.CombineLatest(neighbourIds.Prev, neighbourIds.Next, static (direction, previousId, nextId) => (direction, previousId, nextId) switch
        {
            (ListSortDirection.Ascending, previousId: { Value: > 0 }, _) => true,
            (ListSortDirection.Descending, _, nextId: { Value: > 0}) => true,
            _ => false,
        });

        var canExecuteMoveDown = _sortDirectionObservable.CombineLatest(neighbourIds.Prev, neighbourIds.Next, static (direction, previousId, nextId) => (direction, previousId, nextId) switch
        {
            (ListSortDirection.Ascending, _, nextId: { Value: > 0}) => true,
            (ListSortDirection.Descending, previousId: { Value: > 0 }, _) => true,
            _ => false,
        });

        return new SharedComponents.IndexComponent(
            new ValueComponent<int>(index),
            new ValueComponent<string>(display),
            canExecuteMoveUp: canExecuteMoveUp,
            canExecuteMoveDown: canExecuteMoveDown
        );
    }

    private CompositeItemModel<EntityId> CreateCollectionModel((EntityId Id, long Lowest, long Highest, EntityId Prev, EntityId Next) tuple)
    {
        var itemModel = new CompositeItemModel<EntityId>(tuple.Id);
        itemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: LoadoutItem.Load(_connection.Db, tuple.Id).Name));
        itemModel.Add(SharedColumns.Name.ImageComponentKey, new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback));

        // Conflicts belong to the mods; the collection row only carries the rules action.
        itemModel.Add(FileConflictsColumns.Actions.ViewComponentKey, new FileConflictsComponents.ViewAction(hasConflicts: false));
        itemModel.Add(FileConflictsColumns.IndexColumn.UnitAnchorComponentKey, new FileConflictsComponents.UnitAnchor((int)tuple.Lowest, (int)tuple.Highest));

        var neighbourIds = new FileConflictsComponents.NeighbourIds(tuple.Prev, tuple.Next);
        itemModel.Add(FileConflictsColumns.IndexColumn.NeighbourIdsComponentKey, neighbourIds);
        itemModel.Add(FileConflictsColumns.IndexColumn.IndexComponentKey, CreateIndexComponent((int)tuple.Lowest, string.Empty, neighbourIds));
        itemModel.Add(FileConflictsColumns.IsActiveComponentKey, new ValueComponent<bool>(true));
        return itemModel;
    }

    private static void UpdateCollectionModel(CompositeItemModel<EntityId> itemModel, (EntityId Id, long Lowest, long Highest, EntityId Prev, EntityId Next) tuple)
    {
        var anchor = itemModel.Get<FileConflictsComponents.UnitAnchor>(FileConflictsColumns.IndexColumn.UnitAnchorComponentKey);
        anchor.Lowest.Value = (int)tuple.Lowest;
        anchor.Highest.Value = (int)tuple.Highest;

        var neighbourIds = itemModel.Get<FileConflictsComponents.NeighbourIds>(FileConflictsColumns.IndexColumn.NeighbourIdsComponentKey);
        neighbourIds.Prev.Value = tuple.Prev;
        neighbourIds.Next.Value = tuple.Next;

        itemModel.Get<SharedComponents.IndexComponent>(FileConflictsColumns.IndexColumn.IndexComponentKey).Index.Value.Value = (int)tuple.Lowest;
    }

    private CompositeItemModel<EntityId> CreateItemModel((EntityId, long, EntityId, EntityId, long, long) tuple)
    {
        var db = _connection.Db;
        var (groupPriorityId, index, previousId, nextId, numWinningFiles, numLosingFiles) = tuple;

        var groupPriority = LoadoutItemGroupPriority.Load(db, groupPriorityId);
        var loadoutGroup = groupPriority.Target;

        var itemModel = new CompositeItemModel<EntityId>(groupPriorityId);

        itemModel.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: loadoutGroup.AsLoadoutItem().Name));
        ImageComponent? imageComponent = null;

        if (loadoutGroup.TryGetAsLibraryLinkedLoadoutItem(out var libraryLinkedLoadoutItem))
        {
            if (libraryLinkedLoadoutItem.LibraryItem.TryGetAsNexusModsLibraryItem(out var nexusLibraryItem))
            {
                imageComponent = ImageComponent.FromPipeline(
                    _modPageThumbnailPipeline, 
                    nexusLibraryItem.ModPageMetadataId, 
                    ImagePipelines.ModPageThumbnailFallback);
            }
        }

        imageComponent ??= new ImageComponent(value: ImagePipelines.ModPageThumbnailFallback);
        itemModel.Add(SharedColumns.Name.ImageComponentKey, imageComponent);

        itemModel.Add(FileConflictsColumns.ConflictsColumn.NumConflictsComponentKey, new FileConflictsComponents.NumConflicts(
            numWinners: new ValueComponent<long>(value: numWinningFiles),
            numLosers: new ValueComponent<long>(value: numLosingFiles)
        ));

        // A group made outside an install (an empty group, the overrides mover's) has no parent at all.
        if (loadoutGroup.AsLoadoutItem().HasParent() && loadoutGroup.AsLoadoutItem().Parent.TryGetAsCollectionGroup(out var collection))
        {
            itemModel.Add(LoadoutColumns.Collections.ComponentKey, new StringComponent(value: collection.AsLoadoutItemGroup().AsLoadoutItem().Name));
        }

        itemModel.Add(FileConflictsColumns.Actions.ViewComponentKey, new FileConflictsComponents.ViewAction(hasConflicts: numWinningFiles > 0 || numLosingFiles > 0));

        var neighbourIds = new FileConflictsComponents.NeighbourIds(previousId, nextId);
        itemModel.Add(FileConflictsColumns.IndexColumn.NeighbourIdsComponentKey, neighbourIds);

        itemModel.Add(FileConflictsColumns.IndexColumn.IndexComponentKey, CreateIndexComponent((int)index, ((int)index).Ordinalize(), neighbourIds));

        // NOTE(Al12rs): Mark all items as active for styling purposes, change this if we need to display inactive items
        itemModel.Add(FileConflictsColumns.IsActiveComponentKey, new ValueComponent<bool>(true));

        return itemModel;
    }

    /// <summary>
    /// Adds the files the mod shares with another me3 package to the synchronizer's counts. This walks every item of
    /// the loadout, so it runs before the changes reach the UI thread. The index is rebuilt only when the database
    /// moved on, since every row of the list asks.
    /// </summary>
    private (EntityId Id, long Index, EntityId Prev, EntityId Next, long NumWinningFiles, long NumLosingFiles) AddSharedFiles(
        (EntityId Id, long Index, EntityId Prev, EntityId Next, long NumWinningFiles, long NumLosingFiles) row)
    {
        if (_mergeSource is null) return row;

        var db = _connection.Db;
        Dictionary<LoadoutItemGroupId, (long Winning, long Losing)> counts;
        lock (_sharedFileCountsLock)
        {
            if (_sharedFileCounts is not { } known || known.Basis != db.BasisTxId)
            {
                var loadout = Loadout.Load(db, _loadoutId);
                known = (db.BasisTxId, _mergeSource.ProvidesIndex(loadout) && _mergeSource.IndexOf(loadout) is { } index
                    ? FileConflictPaths.SharedFileCounts(index)
                    : []);
                _sharedFileCounts = known;
            }

            counts = known.Counts;
        }

        var group = LoadoutItemGroupPriority.Load(db, row.Id).TargetId;
        return counts.TryGetValue(group, out var shared)
            ? row with { NumWinningFiles = row.NumWinningFiles + shared.Winning, NumLosingFiles = row.NumLosingFiles + shared.Losing }
            : row;
    }

    private void UpdateItemModel(CompositeItemModel<EntityId> itemModel, (EntityId Id, long Index, EntityId Prev, EntityId Next, long NumWinningFiles, long NumLosingFiles) tuple)
    {
        var (id, index, previousId, nextId, numWinningFiles, numLosingFiles) = tuple;

        itemModel.Get<FileConflictsComponents.ViewAction>(FileConflictsColumns.Actions.ViewComponentKey).HasConflicts.Value = numWinningFiles > 0 || numLosingFiles > 0;

        var numConflicts = itemModel.Get<FileConflictsComponents.NumConflicts>(FileConflictsColumns.ConflictsColumn.NumConflictsComponentKey);
        numConflicts.NumWinners.Value.Value = numWinningFiles;
        numConflicts.NumLosers.Value.Value = numLosingFiles;

        var neighbourIds = itemModel.Get<FileConflictsComponents.NeighbourIds>(FileConflictsColumns.IndexColumn.NeighbourIdsComponentKey);
        neighbourIds.Prev.Value = previousId;
        neighbourIds.Next.Value = nextId;

        var indexComponent = itemModel.Get<SharedComponents.IndexComponent>(FileConflictsColumns.IndexColumn.IndexComponentKey);
        indexComponent.Index.Value.Value = (int)index;
        indexComponent.DisplaySortIndexComponent.Value.Value = ((int)index).Ordinalize();
    }

    internal static Query<(EntityId WinnerPriorityId, EntityId WinnerLoadoutItemId, EntityId ConflictPriorityId, EntityId ConflictLoadoutItemId, EntityId LoserPriorityId, EntityId LoserLoadoutItemId)> GetPriorityConflicts(
        IConnection connection,
        EntityId priorityId)
    {
        // NOTE(erri120): limited by our current query engine to not supported nested lists and tuples so we have to unnest
        return connection.Query<(EntityId, EntityId, EntityId, EntityId, EntityId, EntityId)>(
            $"""
             SELECT
               Winner.PriorityId,
               Winner.LoadoutItemId,
               conflicts."unnest".PriorityId,
               conflicts."unnest".LoadoutItemId,
               losers."unnest".PriorityId,
               losers."unnest".LoadoutItemId
             FROM
               synchronizer.ConflictingPaths ({connection}) conflicting_path
               CROSS JOIN unnest(conflicting_path.Conflicts) conflicts
               CROSS JOIN unnest(conflicting_path.Losers) losers
             WHERE
               len(list_filter(conflicting_path.Conflicts, x -> x.PriorityId = {priorityId})) > 0;
             """
        );
    }

    /// <summary>The collections with a mod in the list: the places of their lowest and highest mods, and the rows beside them.</summary>
    private static IObservable<IChangeSet<(EntityId Id, long Lowest, long Highest, EntityId Prev, EntityId Next), EntityId>> ObserveCollections(IConnection connection, LoadoutId loadoutId)
    {
        return connection.Query<(EntityId Id, long, long, EntityId, EntityId)>(
            $"""
             SELECT
               item.Parent,
               min(priority.Index),
               max(priority.Index),
               arg_min(priority.Prev, priority.Index),
               arg_max(priority.Next, priority.Index)
             FROM
               synchronizer.PriorityGroups({connection}) priority
               JOIN mdb_LoadoutItem(Db=>{connection}) item ON item.Id = priority.Target
               JOIN mdb_CollectionGroup(Db=>{connection}) collection ON collection.Id = item.Parent
             WHERE priority.Loadout = {loadoutId}
             GROUP BY item.Parent;
             """
        ).Observe(tuple => tuple.Id);
    }

    private static IObservable<IChangeSet<(EntityId Id, long Index, EntityId Prev, EntityId Next, long NumWinningFiles, long NumLosingFiles), EntityId>> ObservePriorityGroups(IConnection connection, LoadoutId loadoutId)
    {
        return connection.Query<(EntityId Id, long, EntityId, EntityId, long, long)>(
            $"""
             SELECT Id, Index, Prev, Next, len(WinningFiles), len(LosingFiles)
             FROM
               synchronizer.PriorityGroups({connection})
             WHERE Loadout = {loadoutId};
             """
        ).Observe(tuple => tuple.Id);
    }

    protected override IColumn<CompositeItemModel<EntityId>>[] CreateColumns(bool viewHierarchical)
    {
        var indexColumn = ColumnCreator.Create<EntityId, FileConflictsColumns.IndexColumn>(canUserSortColumn: false, canUserResizeColumn: false);

        return
        [
            ITreeDataGridItemModel<CompositeItemModel<EntityId>, EntityId>.CreateExpanderColumn(indexColumn),
            ColumnCreator.Create<EntityId, SharedColumns.Name>(canUserSortColumn: false),
            ColumnCreator.Create<EntityId, FileConflictsColumns.ConflictsColumn>(canUserSortColumn: false),
            ColumnCreator.Create<EntityId, LoadoutColumns.Collections>(canUserSortColumn: false),
            ColumnCreator.Create<EntityId, FileConflictsColumns.Actions>(canUserSortColumn: false),
        ];
    }

    private bool _isDisposed;
    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                R3.Disposable.Dispose(_activationDisposable, MessageSubject);
            }

            _isDisposed = true;
        }

        base.Dispose(disposing);
    }
}
