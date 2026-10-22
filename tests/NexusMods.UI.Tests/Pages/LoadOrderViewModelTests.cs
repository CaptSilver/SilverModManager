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
using NexusMods.App.UI.Pages.Sorting;
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
        public IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId) => throw new NotSupportedException();
        public Task MoveItems(SortOrderId sortOrderId, ISortItemKey[] itemsToMove, ISortItemKey dropTargetItem, TargetRelativePosition relativePosition, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task MoveItemDelta(SortOrderId sortOrderId, ISortItemKey sourceItem, int delta, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class FakePluginVariety(params IReactiveSortItem[] items) : FakeVariety(items), IToggleableSortOrderVariety, IReplacedGameEditSource
    {
        public List<(ISortItemKey Key, bool Enabled)> Switched { get; } = [];
        public int Dismissals { get; private set; }
        public BehaviorSubject<string?> Summaries { get; } = new(null);

        public Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default)
        {
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
    public async Task ToggleEnabledCommand_ForARowThatIsGone_SwitchesNothing()
    {
        var variety = new FakePluginVariety();
        var vm = NewViewModel(variety);

        await vm.ToggleEnabledCommand.Execute(PluginKey);

        variety.Switched.Should().BeEmpty();
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
