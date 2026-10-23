using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using DynamicData;
using DynamicData.Binding;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.App.UI.Controls;
using R3;
using NexusMods.App.UI.Controls.Alerts;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using CompositeDisposable = System.Reactive.Disposables.CompositeDisposable;
using ReactiveCommand = ReactiveUI.ReactiveCommand;
using Unit = System.Reactive.Unit;
using OneOf;

namespace NexusMods.App.UI.Pages.Sorting;

public class LoadOrderViewModel : AViewModel<ILoadOrderViewModel>, ILoadOrderViewModel
{
    public ISortOrderVariety SortOrderVariety { get; }
    public string SortOrderName { get; }
    public string InfoAlertTitle { get; }
    public string InfoAlertBody { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> ToggleAlertCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> LearnMoreAlertCommand { get; }
    public string TrophyToolTip { get; }
    [Reactive] public ListSortDirection SortDirectionCurrent { get; set; }
    
    public ReactiveUI.ReactiveCommand<Unit, Unit> SwitchSortDirectionCommand { get; }
    
    [Reactive] public bool IsAscending { get; private set; }
    [Reactive] public bool IsWinnerTop { get; private set; }
    public string EmptyStateMessageTitle { get; }
    public string EmptyStateMessageContents { get; }

    public AlertSettingsWrapper AlertSettingsWrapper { get; }

    public bool SupportsToggle { get; }
    public ReactiveUI.ReactiveCommand<ISortItemKey, Unit> ToggleEnabledCommand { get; }
    [Reactive] public string? ReplacedEditSummary { get; private set; }
    [Reactive] public bool HasReplacedEdit { get; private set; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> DismissReplacedEditCommand { get; }
    [Reactive] public string? HoldSummary { get; private set; }
    [Reactive] public bool HasHold { get; private set; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> ReleaseHoldCommand { get; }

    public bool SupportsLootSort { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> SortWithLootCommand { get; }
    [Reactive] public bool HasLootOutcome { get; private set; }
    [Reactive] public string? LootOutcomeTitle { get; private set; }
    [Reactive] public string? LootOutcomeSummary { get; private set; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> DismissLootOutcomeCommand { get; }

    public TreeDataGridAdapter<CompositeItemModel<ISortItemKey>, ISortItemKey> Adapter { get; }

    private readonly ILogger<LoadOrderViewModel> _logger;
    private readonly ISortOrderVariety _variety;
    private readonly SortOrderId _sortOrderId;

    /// <summary>
    /// Runs a change to the order. A row can be listed before it is stored (a plugin that joined the loadout since
    /// the last sync), so the change fails; the user sees the error in the panel the LOOT sort uses, instead of it
    /// reaching the unhandled-exception handler.
    /// </summary>
    private async Task RunEdit(Func<Task> edit)
    {
        try
        {
            await edit();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Changing the {Variety} order failed", _variety.SortOrderVarietyId);
            LootOutcomeTitle = Language.LoadOrder_EditFailedTitle;
            LootOutcomeSummary = e.Message;
            HasLootOutcome = true;
        }
    }

    /// <summary>Moves rows dropped on <paramref name="target"/>, showing the error instead of throwing when it fails.</summary>
    internal Task MoveDropped(ISortItemKey[] keys, ISortItemKey target, TargetRelativePosition position, CancellationToken token) =>
        RunEdit(() => _variety.MoveItems(_sortOrderId, keys, target, position, token: token));

    [UsedImplicitly]
    public LoadOrderViewModel(
        IServiceProvider serviceProvider,
        ISortOrderVariety sortOrderVariety,
        LoadoutId loadoutId)
    {
        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();

        SortOrderVariety = sortOrderVariety;
        _variety = sortOrderVariety;
        _logger = serviceProvider.GetRequiredService<ILogger<LoadOrderViewModel>>();
        var optionalSortOrderId = sortOrderVariety.GetSortOrderIdFor(loadoutId);
        if (!optionalSortOrderId.HasValue)
            throw new InvalidOperationException($"No sort order found for loadout {loadoutId} and variety {sortOrderVariety.SortOrderVarietyId}");
        var sortOrderId = optionalSortOrderId.Value;
        _sortOrderId = sortOrderId;
        
        SortOrderName = sortOrderVariety.SortOrderUiMetadata.SortOrderName;
        InfoAlertTitle = sortOrderVariety.SortOrderUiMetadata.OverrideInfoTitle;
        InfoAlertBody = sortOrderVariety.SortOrderUiMetadata.OverrideInfoMessage;
        TrophyToolTip = sortOrderVariety.SortOrderUiMetadata.WinnerIndexToolTip;
        EmptyStateMessageTitle = sortOrderVariety.SortOrderUiMetadata.EmptyStateMessageTitle;
        EmptyStateMessageContents = sortOrderVariety.SortOrderUiMetadata.EmptyStateMessageContents;

        // TODO: load these from settings
        SortDirectionCurrent = sortOrderVariety.SortDirectionDefault;
        IsAscending = SortDirectionCurrent == ListSortDirection.Ascending;

        IsWinnerTop = SortDirectionCurrent == ListSortDirection.Ascending &&
                      sortOrderVariety.IndexOverrideBehavior == IndexOverrideBehavior.SmallerIndexWins;

        var sortDirectionObservable = this.WhenAnyValue(vm => vm.SortDirectionCurrent)
            .Replay(1);

        var adapter = new LoadOrderTreeDataGridAdapter(sortOrderVariety, loadoutId, sortDirectionObservable, serviceProvider);
        Adapter = adapter;
        Adapter.ViewHierarchical.Value = true;

        // We have different alerts based on the type of load order, so we key in the SortOrderTypeId
        AlertSettingsWrapper = new AlertSettingsWrapper(settingsManager, $"LoadOrder Alert Type:{sortOrderVariety.SortOrderVarietyId}");
        
        ToggleAlertCommand = ReactiveCommand.Create(() => { AlertSettingsWrapper.ToggleAlert(); });
        
        LearnMoreAlertCommand = ReactiveCommand.Create(() => osInterop.OpenUri(new Uri(sortOrderVariety.SortOrderUiMetadata.LearnMoreUrl)));

        SwitchSortDirectionCommand = ReactiveCommand.Create(() =>
            {
                SortDirectionCurrent = IsAscending
                    ? ListSortDirection.Descending 
                    : ListSortDirection.Ascending;
            }
        );

        var toggleable = sortOrderVariety as IToggleableSortOrderVariety;
        SupportsToggle = toggleable is not null;
        ToggleEnabledCommand = ReactiveCommand.CreateFromTask<ISortItemKey>(
            async key =>
            {
                if (toggleable is null)
                    throw new InvalidOperationException($"{sortOrderVariety.SortOrderVarietyId} has no switches to toggle");

                // The row can be gone by the time a click is handled, as when its mod was just removed.
                var item = sortOrderVariety.GetSortOrderItems(sortOrderId).FirstOrDefault(item => item.Key.Equals(key));
                if (item is null) return;
                if (item is not ISortItemDetails details)
                    throw new InvalidOperationException($"Item {key} of {sortOrderVariety.SortOrderVarietyId} has no switch");

                // The switch is disabled for these rows already; this covers a stale click.
                if (details.IsLocked || details.LockReason is not null) return;
                await RunEdit(() => toggleable.SetEnabled(sortOrderId, key, !details.IsEnabled));
            },
            canExecute: System.Reactive.Linq.Observable.Return(SupportsToggle)
        );

        var replacedEdits = sortOrderVariety as IReplacedGameEditSource;
        DismissReplacedEditCommand = ReactiveCommand.CreateFromTask(
            () => replacedEdits?.Dismiss(sortOrderId)
                  ?? throw new InvalidOperationException($"{sortOrderVariety.SortOrderVarietyId} has no replaced-edit notice to dismiss"),
            canExecute: System.Reactive.Linq.Observable.Return(replacedEdits is not null)
        );

        var heldEdits = sortOrderVariety as IHeldGameEditSource;
        ReleaseHoldCommand = ReactiveCommand.CreateFromTask(
            () => RunEdit(() => heldEdits?.Release(sortOrderId)
                                ?? throw new InvalidOperationException($"{sortOrderVariety.SortOrderVarietyId} holds nothing back to release")),
            canExecute: System.Reactive.Linq.Observable.Return(heldEdits is not null)
        );

        var lootSource = sortOrderVariety as ILootSortSource;
        SupportsLootSort = lootSource is not null;
        SortWithLootCommand = ReactiveCommand.CreateFromTask(
            async () =>
            {
                if (lootSource is null)
                    throw new InvalidOperationException($"{sortOrderVariety.SortOrderVarietyId} cannot be sorted with LOOT");

                LootSortOutcome outcome;
                try
                {
                    outcome = await lootSource.SortAsync(sortOrderId);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Bad metadata (an unknown group, a rule that contradicts master flags, a broken userlist) is
                    // something the user can fix, so it goes in the panel rather than only into the log.
                    serviceProvider.GetRequiredService<ILogger<LoadOrderViewModel>>().LogError(e, "Sorting {Variety} with LOOT failed", sortOrderVariety.SortOrderVarietyId);
                    LootOutcomeTitle = Language.LoadOrder_SortWithLoot_FailedTitle;
                    LootOutcomeSummary = e.Message;
                    HasLootOutcome = true;
                    return;
                }

                LootOutcomeTitle = outcome.Succeeded ? Language.LoadOrder_SortWithLoot_Title : Language.LoadOrder_SortWithLoot_FailedTitle;
                LootOutcomeSummary = LootOutcomeText.Describe(outcome);
                HasLootOutcome = true;

                if (!outcome.Succeeded)
                    await ShowCycleDialog(serviceProvider, outcome);
            },
            canExecute: System.Reactive.Linq.Observable.Return(SupportsLootSort)
        );

        DismissLootOutcomeCommand = ReactiveCommand.Create(() =>
        {
            HasLootOutcome = false;
            LootOutcomeTitle = null;
            LootOutcomeSummary = null;
        });

        this.WhenActivated(d =>
            {
                Adapter.Activate().DisposeWith(d);

                replacedEdits?.ReplacedEditSummary(sortOrderId)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(summary =>
                        {
                            ReplacedEditSummary = summary;
                            HasReplacedEdit = summary is not null;
                        })
                    .DisposeWith(d);

                heldEdits?.HoldSummary(sortOrderId)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(summary =>
                        {
                            HoldSummary = summary;
                            HasHold = summary is not null;
                        })
                    .DisposeWith(d);

                sortDirectionObservable.Connect()
                    .DisposeWith(d);

                // Update IsWinnerTop
                sortDirectionObservable.Subscribe(sortDirection =>
                    {
                        IsAscending = sortDirection == ListSortDirection.Ascending;
                        IsWinnerTop = IsAscending &&
                                      sortOrderVariety.IndexOverrideBehavior == IndexOverrideBehavior.SmallerIndexWins;
                    })
                    .DisposeWith(d);

                // Move up/down commands
                adapter.MessageSubject
                    .SubscribeAwait(async (payload, cancellationToken) =>
                        {
                            var (key, delta) = payload.Match(
                                moveUpPayload =>
                                {
                                    var deltaUp = SortDirectionCurrent == ListSortDirection.Ascending ? -1 : +1;
                                    return (moveUpPayload.Item.Key, deltaUp);
                                },
                                moveDownPayload =>
                                {
                                    var deltaDown = SortDirectionCurrent == ListSortDirection.Ascending ? +1 : -1;
                                    return (moveDownPayload.Item.Key, deltaDown);
                                }
                            );
                            
                            await RunEdit(() => sortOrderVariety.MoveItemDelta(sortOrderId, key, delta, token: cancellationToken));
                        })
                    .DisposeWith(d);

                // Switches
                adapter.ToggleSubject
                    .SubscribeAwait(async (key, _) => await ToggleEnabledCommand.Execute(key),
                        awaitOperation: AwaitOperation.Sequential)
                    .DisposeWith(d);

                // Drag and drop
                adapter.RowDragOverSubject
                    .Subscribe(dragDropPayload =>
                        {
                            var (sourceModels, targetModel, eventArgs) = dragDropPayload;

                            if (eventArgs.Position != TreeDataGridRowDropPosition.Inside) return;
                            
                            // Update the drop position for the inside case to be before or after
                            eventArgs.Position = PointerIsInVerticalTopHalf(eventArgs) ? 
                                TreeDataGridRowDropPosition.Before : TreeDataGridRowDropPosition.After;
                        }
                    )
                    .DisposeWith(d);
                
                adapter.RowDropSubject
                    .SubscribeAwait(async (dragDropPayload, cancellationToken) =>
                        {
                            var (sourceModels, targetModel, _, _, eventArgs) = dragDropPayload;

                            // Determine source items
                            var keysToMove = sourceModels.Select(item => item.Key).ToArray();
                            if (keysToMove.Length == 0) return;

                            // Determine target item
                            var dropTargetKey = targetModel.Key;
                            
                            // Determine relative position
                            TargetRelativePosition relativePosition;
                            switch (eventArgs.Position)
                            {
                                case TreeDataGridRowDropPosition.Before when SortDirectionCurrent == ListSortDirection.Ascending:
                                case TreeDataGridRowDropPosition.After when SortDirectionCurrent == ListSortDirection.Descending:
                                    relativePosition = TargetRelativePosition.BeforeTarget;
                                    break;
                                case TreeDataGridRowDropPosition.After when SortDirectionCurrent == ListSortDirection.Ascending:
                                case TreeDataGridRowDropPosition.Before when SortDirectionCurrent == ListSortDirection.Descending:
                                    relativePosition = TargetRelativePosition.AfterTarget;
                                    break;
                                case TreeDataGridRowDropPosition.Inside when SortDirectionCurrent == ListSortDirection.Ascending:
                                    relativePosition = PointerIsInVerticalTopHalf(eventArgs) ? TargetRelativePosition.BeforeTarget : TargetRelativePosition.AfterTarget;
                                    break;
                                case TreeDataGridRowDropPosition.Inside when SortDirectionCurrent == ListSortDirection.Descending:
                                    relativePosition = PointerIsInVerticalTopHalf(eventArgs) ? TargetRelativePosition.AfterTarget : TargetRelativePosition.BeforeTarget;
                                    break;
                                case TreeDataGridRowDropPosition.None:
                                    // Invalid target, no move
                                    return;
                                default:
                                    return;
                            }
                            
                            await MoveDropped(keysToMove, dropTargetKey, relativePosition, cancellationToken);
                        },
                        awaitOperation: AwaitOperation.Drop)
                    .DisposeWith(d);
            }
        );
    }

    private static async Task ShowCycleDialog(IServiceProvider serviceProvider, LootSortOutcome outcome)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            Language.LoadOrder_SortWithLoot_FailedTitle,
            new StandardDialogParameters
            {
                Text = string.Format(Language.LoadOrder_SortWithLoot_CycleDialog_Text, outcome.CycleText),
            },
            [DialogStandardButtons.Ok]
        );
        await serviceProvider.GetRequiredService<IWindowManager>().ShowDialog(dialog, DialogWindowType.Modal);
    }

    private static bool PointerIsInVerticalTopHalf(TreeDataGridRowDragEventArgs eventArgs)
    {
        var positionY = eventArgs.Inner.GetPosition(eventArgs.TargetRow).Y / eventArgs.TargetRow.Bounds.Height;
        return positionY < 0.5;
    }
}



public class LoadOrderTreeDataGridAdapter : TreeDataGridAdapter<CompositeItemModel<ISortItemKey>, ISortItemKey>,
    ITreeDataGirdMessageAdapter<OneOf<LoadOrderTreeDataGridAdapter.MoveUpCommandPayload, LoadOrderTreeDataGridAdapter.MoveDownCommandPayload>>
{
    public readonly record struct MoveUpCommandPayload(CompositeItemModel<ISortItemKey> Item);
    public readonly record struct MoveDownCommandPayload(CompositeItemModel<ISortItemKey> Item);
    
    private readonly ISortOrderVariety _sortOrderVariety;
    private readonly LoadoutId _loadoutId;
    private readonly ILoadOrderDataProvider[] _loadOrderDataProviders;
    private readonly R3.Observable<ListSortDirection> _sortDirectionObservable;
    private readonly System.Reactive.Subjects.Subject<IComparer<CompositeItemModel<ISortItemKey>>> _resortSubject = new(); 
    private readonly CompositeDisposable _disposables = new();

    public Subject<OneOf<MoveUpCommandPayload, MoveDownCommandPayload>> MessageSubject { get; } = new();

    /// <summary>
    /// Emits the key of a row whose switch the user clicked.
    /// </summary>
    public Subject<ISortItemKey> ToggleSubject { get; } = new();

    public LoadOrderTreeDataGridAdapter(
        ISortOrderVariety sortOrderVariety,
        LoadoutId loadoutId,
        IObservable<ListSortDirection> sortDirectionObservable,
        IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _loadoutId = loadoutId;
        _sortOrderVariety = sortOrderVariety;
        _sortDirectionObservable = sortDirectionObservable.ToObservable();

        _loadOrderDataProviders = serviceProvider.GetServices<ILoadOrderDataProvider>().ToArray();
        
        var ascendingComparer = SortExpressionComparer<CompositeItemModel<ISortItemKey>>.Ascending(
            item => item.Get<SharedComponents.IndexComponent>(LoadOrderColumns.IndexColumn.IndexComponentKey).SortIndex.Value
        );
        var descendingComparer = SortExpressionComparer<CompositeItemModel<ISortItemKey>>.Descending(
            item => item.Get<SharedComponents.IndexComponent>(LoadOrderColumns.IndexColumn.IndexComponentKey).SortIndex.Value
        );
        
        var comparerObservable = sortDirectionObservable.Select(sortDirection =>
            {
                return sortDirection == ListSortDirection.Ascending
                    ? ascendingComparer
                    : descendingComparer;
            }
        );

        var activationDisposable = this.WhenActivated( (self, disposables)  =>
            {
                // Sort the Roots list when the sorting direction changes, as it doesn't update correctly otherwise
                comparerObservable
                    .Subscribe(comparer => CustomSortComparer.Value = comparer)
                    .AddTo(disposables);
            }
        );
        activationDisposable.DisposeWith(_disposables);
    }

    protected override void BeforeModelActivationHook(CompositeItemModel<ISortItemKey> model)
    {
        base.BeforeModelActivationHook(model);
        
        // Move up command
        model.SubscribeToComponentAndTrack<SharedComponents.IndexComponent, LoadOrderTreeDataGridAdapter>(
            key: LoadOrderColumns.IndexColumn.IndexComponentKey,
            state: this,
            factory: static (adapter, itemModel, component) => component.MoveUp
                .Subscribe((adapter, itemModel, component),
                    static (_, tuple) =>
                    {
                        var (adapter, itemModel, _) = tuple;
                        adapter.MessageSubject.OnNext(new MoveUpCommandPayload(itemModel));
                    }
                )
        );
        
        // Move down command
        model.SubscribeToComponentAndTrack<SharedComponents.IndexComponent, LoadOrderTreeDataGridAdapter>(
            key: LoadOrderColumns.IndexColumn.IndexComponentKey,
            state: this,
            factory: static (adapter, itemModel, component) => component.MoveDown
                .Subscribe((adapter, itemModel, component),
                    static (_, tuple) =>
                    {
                        var (adapter, itemModel, _) = tuple;
                        adapter.MessageSubject.OnNext(new MoveDownCommandPayload(itemModel));
                    }
                )
        );
        
        // Switch
        model.SubscribeToComponentAndTrack<LoadOrderToggleComponent, LoadOrderTreeDataGridAdapter>(
            key: LoadOrderColumns.EnabledColumn.EnabledComponentKey,
            state: this,
            factory: static (adapter, itemModel, component) => component.Toggle
                .Subscribe((adapter, itemModel),
                    static (_, tuple) => tuple.adapter.ToggleSubject.OnNext(tuple.itemModel.Key)
                )
        );

        // IsActive styling
        model.SubscribeToComponentAndTrack<ValueComponent<bool>, LoadOrderTreeDataGridAdapter>(
            key: LoadOrderColumns.IsActiveComponentKey,
            state: this,
            factory: static (adapter, itemModel, component) => component.Value
                .Subscribe((adapter, itemModel, component),
                    static (_, tuple) =>
                    {
                        var (_, itemModel, component) = tuple;
                        itemModel.SetStyleFlag(LoadOrderColumns.IsActiveStyleTag, component.Value.Value);
                    }
                )
        );
    }

    protected override IObservable<IChangeSet<CompositeItemModel<ISortItemKey>, ISortItemKey>> GetRootsObservable(bool viewHierarchical)
    {
        return _loadOrderDataProviders
            .Select(x => x.ObserveLoadOrder(_sortOrderVariety, _loadoutId, _sortDirectionObservable))
            .MergeChangeSets();
    }

    protected override IColumn<CompositeItemModel<ISortItemKey>>[] CreateColumns(bool viewHierarchical)
    {
        var indexColumn = ColumnCreator.Create<ISortItemKey, LoadOrderColumns.IndexColumn>(
            columnHeader: _sortOrderVariety.SortOrderUiMetadata.IndexColumnHeader,
            canUserSortColumn: false,
            canUserResizeColumn: false
        );
        
        var expanderColumn = ITreeDataGridItemModel<CompositeItemModel<ISortItemKey>, ISortItemKey>.CreateExpanderColumn(indexColumn);

        var columns = new List<IColumn<CompositeItemModel<ISortItemKey>>>
        {
            expanderColumn,
            ColumnCreator.Create<ISortItemKey, LoadOrderColumns.DisplayNameColumn>(
                columnHeader: _sortOrderVariety.SortOrderUiMetadata.DisplayNameColumnHeader,
                canUserSortColumn: false,
                canUserResizeColumn: false
            ),
        };

        // Only a variety whose rows can be switched has anything to show in these two.
        if (_sortOrderVariety is IToggleableSortOrderVariety)
        {
            columns.Add(ColumnCreator.Create<ISortItemKey, LoadOrderColumns.EnabledColumn>(
                columnHeader: Language.LoadOrder_EnabledColumnHeader,
                canUserSortColumn: false,
                canUserResizeColumn: false
            ));
            columns.Add(ColumnCreator.Create<ISortItemKey, LoadOrderColumns.DetailsColumn>(
                columnHeader: Language.LoadOrder_DetailsColumnHeader,
                canUserSortColumn: false,
                canUserResizeColumn: false
            ));
        }

        columns.Add(ColumnCreator.Create<ISortItemKey, LoadOrderColumns.ModNameColumn>(
            canUserSortColumn: false,
            canUserResizeColumn: false
        ));

        return columns.ToArray();
    }

    private bool _isDisposed;

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                R3.Disposable.Dispose(_disposables, MessageSubject, ToggleSubject);
            }

            _isDisposed = true;
        }

        base.Dispose(disposing);
    }
}
