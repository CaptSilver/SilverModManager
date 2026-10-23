using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.MnemonicDB.Abstractions;
using OneOf;
using System.ComponentModel;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Pages.Sorting;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;
using NSubstitute;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Microsoft.Extensions.DependencyInjection;

namespace NexusMods.UI.Tests.Pages;

/// <summary>
/// What the Load Order page's view model offers beyond moving rows: switching rows on and off, and the notice
/// that the game's own edit of the order was replaced. A variety that offers neither gets neither.
/// </summary>
public class LoadOrderViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly SortOrderId SortOrder = SortOrderId.From(42UL);
    private static readonly SortItemKey<string> PluginKey = new("a.esp");

    private static readonly SortOrderUiMetadata Metadata = new()
    {
        SortOrderName = "Plugins",
        OverrideInfoTitle = "title",
        OverrideInfoMessage = "message",
        WinnerIndexToolTip = "winner",
        IndexColumnHeader = "#",
        DisplayNameColumnHeader = "Plugin",
        EmptyStateMessageTitle = "empty",
        EmptyStateMessageContents = "nothing",
        LearnMoreUrl = "https://example.invalid/",
    };

    // Written by hand because a mocked SortOrderVarietyId cannot be built: the id type has no default value.
    private class FakeVariety(params IReactiveSortItem[] items) : ISortOrderVariety
    {
        public SortOrderVarietyId SortOrderVarietyId { get; } = SortOrderVarietyId.From(Guid.NewGuid());
        public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;
        public IndexOverrideBehavior IndexOverrideBehavior => IndexOverrideBehavior.SmallerIndexWins;
        public SortOrderUiMetadata SortOrderUiMetadata => Metadata;
        public Optional<SortOrderId> GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId> parentEntity, IDb? db = null) => SortOrder;
        public IReadOnlyList<IReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db = null) => items;

        public ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId, OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default) => throw new NotSupportedException();
        public virtual IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId) => throw new NotSupportedException();
        public Exception? MoveFailure { get; init; }
        public Task MoveItems(SortOrderId sortOrderId, ISortItemKey[] itemsToMove, ISortItemKey dropTargetItem, TargetRelativePosition relativePosition, IDb? db = null, CancellationToken token = default) => Task.FromException(MoveFailure ?? new NotSupportedException());
        public Task MoveItemDelta(SortOrderId sortOrderId, ISortItemKey sourceItem, int delta, IDb? db = null, CancellationToken token = default) => Task.FromException(MoveFailure ?? new NotSupportedException());
        public ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class FakePluginVariety(params IReactiveSortItem[] items) : FakeVariety(items), IToggleableSortOrderVariety, IReplacedGameEditSource
    {
        public List<(ISortItemKey Key, bool Enabled)> Switched { get; } = [];
        public Exception? SwitchFailure { get; init; }
        public int Dismissals { get; private set; }
        public BehaviorSubject<string?> Summaries { get; } = new(null);

        public Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default)
        {
            if (SwitchFailure is not null) throw SwitchFailure;
            Switched.Add((item, enabled));
            return Task.CompletedTask;
        }

        public IObservable<string?> ReplacedEditSummary(SortOrderId sortOrderId) => Summaries;

        public Task Dismiss(SortOrderId sortOrderId)
        {
            Dismissals++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHeldVariety(params IReactiveSortItem[] items) : FakeVariety(items), IHeldGameEditSource
    {
        public int Releases { get; private set; }
        public BehaviorSubject<string?> Holds { get; } = new(null);

        public IObservable<string?> HoldSummary(SortOrderId sortOrderId) => Holds;

        public Task Release(SortOrderId sortOrderId)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A variety whose rows reach the page through the change set, the way the real ones do.</summary>
    private sealed class ListedVariety(params IReactiveSortItem[] items) : FakeVariety(items), IToggleableSortOrderVariety
    {
        public Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default) => Task.CompletedTask;

        public override IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId) =>
            items.AsObservableChangeSet(item => item.Key);
    }

    private sealed class FakeLootVariety(LootSortOutcome outcome) : FakeVariety, ILootSortSource
    {
        public int Sorts { get; private set; }
        public Exception? Throws { get; init; }

        public Task<LootSortOutcome> SortAsync(SortOrderId sortOrderId, CancellationToken token = default)
        {
            Sorts++;
            return Throws is null ? Task.FromResult(outcome) : Task.FromException<LootSortOutcome>(Throws);
        }
    }

    private sealed class WindowManagerProvider(IServiceProvider inner, IWindowManager windows) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IWindowManager) ? windows : inner.GetService(serviceType);
    }

    private static readonly LootSortOutcome Sorted = new(true, null, OverlapEdgesSkipped: true, UnevaluatedConditions: 3, MasterlistRevision: "skyrimse 4a3a51a216 (bundled, retrieved 2026-10-22)");
    private static readonly LootSortOutcome Cyclic = new(false, "A.esp --[Masterlist Load After]-> B.esp --[Masterlist Load After]-> A.esp", OverlapEdgesSkipped: true, UnevaluatedConditions: 0, MasterlistRevision: "rev");

    private (LoadOrderViewModel ViewModel, IWindowManager Windows) LootViewModel(LootSortOutcome outcome, out FakeLootVariety variety)
    {
        variety = new FakeLootVariety(outcome);
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept });
        return (new LoadOrderViewModel(new WindowManagerProvider(Provider, windows), variety, LoadoutId.From(1)), windows);
    }

    private sealed class FakeItem(bool isEnabled, bool isLocked) : IReactiveSortItem, ISortItemDetails
    {
        public ISortItemKey Key { get; } = PluginKey;
        public int SortIndex { get; set; }
        public string DisplayName => "A.esp";
        public string ModName { get; set; } = "Mod";
        public Optional<LoadoutItemGroupId> ModGroupId { get; set; }
        public bool IsActive { get; set; } = true;
        public ISortItemLoadoutData? LoadoutData { get; set; }
        public string? Flags => null;
        public IReadOnlyList<string> Masters => [];
        public bool IsLocked { get; } = isLocked;
        public SortItemLockReason? LockReason { get; init; }
        public bool IsEnabled { get; } = isEnabled;
    }

    private LoadOrderViewModel NewViewModel(ISortOrderVariety variety) => new(Provider, variety, LoadoutId.From(1));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the view model should have caught up");
    }

    [Fact]
    public async Task ToggleEnabledCommand_SwitchesAnEnabledRowOff()
    {
        var variety = new FakePluginVariety(new FakeItem(isEnabled: true, isLocked: false));
        var vm = NewViewModel(variety);

        await vm.ToggleEnabledCommand.Execute(PluginKey);

        variety.Switched.Should().Equal([((ISortItemKey)PluginKey, false)]);
    }

    [Fact]
    public async Task ToggleEnabledCommand_SwitchesADisabledRowOn()
    {
        var variety = new FakePluginVariety(new FakeItem(isEnabled: false, isLocked: false));
        var vm = NewViewModel(variety);

        await vm.ToggleEnabledCommand.Execute(PluginKey);

        variety.Switched.Should().Equal([((ISortItemKey)PluginKey, true)]);
    }

    [Fact]
    public async Task ToggleEnabledCommand_RefusesALockedRow()
    {
        var variety = new FakePluginVariety(new FakeItem(isEnabled: true, isLocked: true));
        var vm = NewViewModel(variety);

        await vm.ToggleEnabledCommand.Execute(PluginKey);

        variety.Switched.Should().BeEmpty();
    }

    [Fact]
    public async Task ToggleEnabledCommand_RefusesARowWithALockReasonEvenWhenItCanBeMoved()
    {
        var variety = new FakePluginVariety(new FakeItem(isEnabled: true, isLocked: false) { LockReason = SortItemLockReason.GameInstalledGroup });
        var vm = NewViewModel(variety);

        await vm.ToggleEnabledCommand.Execute(PluginKey);

        variety.Switched.Should().BeEmpty();
    }

    private async Task<CompositeItemModel<ISortItemKey>> RowFor(FakeItem item)
    {
        var provider = new LoadOrderDataProvider(Provider);
        var cache = provider
            .ObserveLoadOrder(new ListedVariety(item), LoadoutId.From(1), R3.Observable.Return(ListSortDirection.Ascending))
            .AsObservableCache();
        await Eventually(() => cache.Count.Should().Be(1));
        return cache.Items.Single();
    }

    [Fact]
    public async Task ALockReasonLocksTheToggleWithItsOwnToolTipAndLeavesTheDragHandleAlone()
    {
        var row = await RowFor(new FakeItem(isEnabled: true, isLocked: false) { LockReason = SortItemLockReason.GameInstalledGroup });

        var toggle = row.Get<LoadOrderToggleComponent>(LoadOrderColumns.EnabledColumn.EnabledComponentKey);
        toggle.IsLocked.Should().BeTrue();
        toggle.ToolTip.Should().Be(Language.LoadOrder_GameInstalledLockedToolTip);
        row.Get<SharedComponents.IndexComponent>(LoadOrderColumns.IndexColumn.IndexComponentKey).IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task AnAlwaysLoadedRowKeepsTheDefaultToolTip()
    {
        var row = await RowFor(new FakeItem(isEnabled: true, isLocked: true) { LockReason = SortItemLockReason.AlwaysLoaded });

        var toggle = row.Get<LoadOrderToggleComponent>(LoadOrderColumns.EnabledColumn.EnabledComponentKey);
        toggle.IsLocked.Should().BeTrue();
        toggle.ToolTip.Should().Be(Language.LoadOrder_LockedToolTip);
        row.Get<SharedComponents.IndexComponent>(LoadOrderColumns.IndexColumn.IndexComponentKey).IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task ALockedRowWithoutAReasonKeepsTheDefaultToolTip()
    {
        var row = await RowFor(new FakeItem(isEnabled: true, isLocked: true));

        row.Get<LoadOrderToggleComponent>(LoadOrderColumns.EnabledColumn.EnabledComponentKey).ToolTip.Should().Be(Language.LoadOrder_LockedToolTip);
    }

    [Fact]
    public async Task AnUnlockedRowWithoutAReasonHasAWorkingToggle()
    {
        var row = await RowFor(new FakeItem(isEnabled: true, isLocked: false));

        var toggle = row.Get<LoadOrderToggleComponent>(LoadOrderColumns.EnabledColumn.EnabledComponentKey);
        toggle.IsLocked.Should().BeFalse();
        toggle.ToolTip.Should().BeNull();
    }

    [Fact]
    public async Task TheHoldBanner_ShowsTheSummaryWhileOneIsHeldAndHidesWhenItClears()
    {
        var variety = new FakeHeldVariety();
        var vm = NewViewModel(variety);
        using var _ = vm.Activator.Activate();

        vm.HasHold.Should().BeFalse();

        variety.Holds.OnNext("The game reset the mod list twice");
        await WaitUntil(() => vm.HasHold);
        vm.HoldSummary.Should().Be("The game reset the mod list twice");

        variety.Holds.OnNext(null);
        await WaitUntil(() => !vm.HasHold);
        vm.HoldSummary.Should().BeNull();
    }

    [Fact]
    public async Task TheHoldBannersButton_ReleasesTheHold()
    {
        var variety = new FakeHeldVariety();
        var vm = NewViewModel(variety);

        await vm.ReleaseHoldCommand.Execute();

        variety.Releases.Should().Be(1);
    }

    [Fact]
    public void AVarietyWithoutAHold_HasNoHoldBannerAndNothingToRelease()
    {
        var vm = NewViewModel(new FakeVariety());

        vm.HasHold.Should().BeFalse();
        var canExecute = true;
        using var _ = vm.ReleaseHoldCommand.CanExecute.Subscribe(value => canExecute = value);
        canExecute.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleEnabledCommand_ForARowThatIsGone_SwitchesNothing()
    {
        var variety = new FakePluginVariety();
        var vm = NewViewModel(variety);

        await vm.ToggleEnabledCommand.Execute(PluginKey);

        variety.Switched.Should().BeEmpty();
    }

    private const string NotPersisted = "Plugin 'a.esp' is not in sort order 42.";

    private static FakePluginVariety VarietyWhoseEditsFail() =>
        new(new FakeItem(isEnabled: true, isLocked: false))
        {
            SwitchFailure = new InvalidOperationException(NotPersisted),
            MoveFailure = new InvalidOperationException(NotPersisted),
        };

    [Fact]
    public async Task ToggleEnabledCommand_ForARowThatIsNotStoredYet_ShowsTheErrorInsteadOfThrowing()
    {
        var vm = NewViewModel(VarietyWhoseEditsFail());

        await vm.ToggleEnabledCommand.Execute(PluginKey);

        vm.HasLootOutcome.Should().BeTrue();
        vm.LootOutcomeTitle.Should().Be(Language.LoadOrder_EditFailedTitle);
        vm.LootOutcomeSummary.Should().Contain(NotPersisted);
    }

    [Fact]
    public async Task MovingARowThatIsNotStoredYet_ShowsTheErrorInsteadOfThrowing()
    {
        var vm = NewViewModel(VarietyWhoseEditsFail());
        using var _ = vm.Activator.Activate();

        ((LoadOrderTreeDataGridAdapter)vm.Adapter).MessageSubject.OnNext(
            new LoadOrderTreeDataGridAdapter.MoveUpCommandPayload(new CompositeItemModel<ISortItemKey>(PluginKey)));

        await WaitUntil(() => vm.HasLootOutcome);
        vm.LootOutcomeTitle.Should().Be(Language.LoadOrder_EditFailedTitle);
        vm.LootOutcomeSummary.Should().Contain(NotPersisted);
    }

    [Fact]
    public async Task DroppingARowThatIsNotStoredYet_ShowsTheErrorInsteadOfThrowing()
    {
        var vm = NewViewModel(VarietyWhoseEditsFail());

        await vm.MoveDropped([PluginKey], new SortItemKey<string>("b.esp"), TargetRelativePosition.AfterTarget, CancellationToken.None);

        vm.HasLootOutcome.Should().BeTrue();
        vm.LootOutcomeTitle.Should().Be(Language.LoadOrder_EditFailedTitle);
        vm.LootOutcomeSummary.Should().Contain(NotPersisted);
    }

    [Fact]
    public void AVarietyWithoutSwitches_HasNoToggleColumnAndCannotToggle()
    {
        var vm = NewViewModel(new FakeVariety());

        vm.SupportsToggle.Should().BeFalse();
        var canExecute = false;
        using var _ = vm.ToggleEnabledCommand.CanExecute.Subscribe(value => canExecute = value);
        canExecute.Should().BeFalse();
    }

    [Fact]
    public async Task TheBanner_ShowsTheSummaryWhileOneIsWaitingAndHidesWhenItClears()
    {
        var variety = new FakePluginVariety();
        var summaries = variety.Summaries;
        var vm = NewViewModel(variety);
        using var _ = vm.Activator.Activate();

        vm.HasReplacedEdit.Should().BeFalse();

        summaries.OnNext("Plugins.txt was edited outside the app");
        await WaitUntil(() => vm.HasReplacedEdit);
        vm.ReplacedEditSummary.Should().Be("Plugins.txt was edited outside the app");

        summaries.OnNext(null);
        await WaitUntil(() => !vm.HasReplacedEdit);
        vm.ReplacedEditSummary.Should().BeNull();
    }

    [Fact]
    public async Task DismissingTheBanner_AcknowledgesTheNotice()
    {
        var variety = new FakePluginVariety();
        var vm = NewViewModel(variety);

        await vm.DismissReplacedEditCommand.Execute();

        variety.Dismissals.Should().Be(1);
    }

    [Fact]
    public void AVarietyWithoutANotice_NeverShowsTheBannerOrDismisses()
    {
        var vm = NewViewModel(new FakeVariety());
        using var _ = vm.Activator.Activate();

        vm.HasReplacedEdit.Should().BeFalse();
        vm.ReplacedEditSummary.Should().BeNull();
        var canExecute = true;
        using var __ = vm.DismissReplacedEditCommand.CanExecute.Subscribe(value => canExecute = value);
        canExecute.Should().BeFalse();
    }

    private static string?[] ColumnHeaders(LoadOrderViewModel vm) =>
        vm.Adapter.Source.Value?.Columns.Select(column => column.Header?.ToString()).ToArray() ?? [];

    [Fact]
    public async Task AVarietyWithoutSwitches_KeepsItsThreeColumns()
    {
        var vm = NewViewModel(new FakeVariety());
        using var _ = vm.Activator.Activate();

        // The adapter builds its grid source on the UI thread.
        await EventuallyOnUi(() => ColumnHeaders(vm).Should().Equal("#", "Plugin", "Parent mod"));
    }

    [Fact]
    public async Task AVarietyWithSwitches_AddsTheSwitchAndDetailsColumnsBeforeTheModName()
    {
        var vm = NewViewModel(new FakePluginVariety());
        using var _ = vm.Activator.Activate();

        // The adapter builds its grid source on the UI thread.
        await EventuallyOnUi(() => ColumnHeaders(vm).Should().Equal("#", "Plugin", "On", "Details", "Parent mod"));
    }

    [Fact]
    public void AVarietyThatSortsWithLoot_OffersTheButton()
    {
        var vm = LootViewModel(Sorted, out _).ViewModel;

        vm.SupportsLootSort.Should().BeTrue();
        var canExecute = false;
        using var subscription = vm.SortWithLootCommand.CanExecute.Subscribe(value => canExecute = value);
        canExecute.Should().BeTrue();
    }

    [Fact]
    public void AVarietyWithoutLoot_HasNoButtonAndCannotSort()
    {
        var vm = NewViewModel(new FakeVariety());

        vm.SupportsLootSort.Should().BeFalse();
        var canExecute = true;
        using var _ = vm.SortWithLootCommand.CanExecute.Subscribe(value => canExecute = value);
        canExecute.Should().BeFalse();
        vm.HasLootOutcome.Should().BeFalse();
    }

    [Fact]
    public async Task Sorting_ShowsTheRevisionAndTheDivergencesAndOpensNoDialog()
    {
        var (vm, windows) = LootViewModel(Sorted, out var variety);

        await vm.SortWithLootCommand.Execute();

        variety.Sorts.Should().Be(1);
        vm.HasLootOutcome.Should().BeTrue();
        vm.LootOutcomeSummary.Should().Contain("skyrimse 4a3a51a216").And.Contain(Language.LoadOrder_SortWithLoot_OverlapSkipped).And.Contain("3 masterlist rules");
        await windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
    }

    [Fact]
    public async Task ACycle_OpensADialogWithTheCycleAndStillShowsThePanel()
    {
        var (vm, windows) = LootViewModel(Cyclic, out _);

        await vm.SortWithLootCommand.Execute();

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
        vm.HasLootOutcome.Should().BeTrue();
        vm.LootOutcomeSummary.Should().Contain("A.esp --[Masterlist Load After]-> B.esp");
        vm.LootOutcomeTitle.Should().NotBe(Language.LoadOrder_SortWithLoot_Title, "the sort did not happen");
    }

    [Fact]
    public async Task ASortThatFailsForAnotherReason_ShowsTheErrorInThePanel()
    {
        var variety = new FakeLootVariety(Sorted) { Throws = new InvalidOperationException("the group \"late\" does not exist") };
        var vm = new LoadOrderViewModel(Provider, variety, LoadoutId.From(1));

        await vm.SortWithLootCommand.Execute();

        vm.HasLootOutcome.Should().BeTrue();
        vm.LootOutcomeTitle.Should().Be(Language.LoadOrder_SortWithLoot_FailedTitle);
        vm.LootOutcomeSummary.Should().Contain("the group \"late\" does not exist");
    }

    [Fact]
    public async Task DismissingThePanel_HidesTheOutcome()
    {
        var (vm, _) = LootViewModel(Sorted, out _);
        await vm.SortWithLootCommand.Execute();

        await vm.DismissLootOutcomeCommand.Execute();

        vm.HasLootOutcome.Should().BeFalse();
        vm.LootOutcomeSummary.Should().BeNull();
    }

    [Fact]
    public void TheSummary_OmitsAnyDivergenceThatDidNotHappen()
    {
        var text = LootOutcomeText.Describe(new LootSortOutcome(true, null, OverlapEdgesSkipped: false, UnevaluatedConditions: 0, MasterlistRevision: "rev 1"));

        text.Should().Contain("rev 1").And.NotContain(Language.LoadOrder_SortWithLoot_OverlapSkipped).And.NotContain("masterlist rules");
    }

    [Fact]
    public void Cyberpunk_ShowsNeitherTogglesNorTheBanner()
    {
        var games = Provider.GetServices<IGame>().ToArray();
        foreach (var other in games) _ = other.SortOrderManager;
        var game = games.OfType<Cyberpunk2077Game>().Single();
        var variety = game.SortOrderManager.GetSortOrderVarieties().Single();

        variety.Should().NotBeAssignableTo<IToggleableSortOrderVariety>();
        variety.Should().NotBeAssignableTo<IReplacedGameEditSource>();
    }
}
