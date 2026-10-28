using System.ComponentModel;
using System.Reactive.Linq;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using OneOf;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Looking a row up in a sort order scans every item of the loadout, so the Load Order page must not do it on the UI
/// thread, neither when a switch is clicked nor when the page opens.
/// </summary>
public class LoadOrderToggleTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly SortItemKey<string> RowKey = new("a.esp");

    private sealed class Row : IReactiveSortItem, ISortItemDetails
    {
        public ISortItemKey Key { get; } = RowKey;
        public int SortIndex { get; set; }
        public string DisplayName => "A.esp";
        public string ModName { get; set; } = "Mod";
        public Optional<LoadoutItemGroupId> ModGroupId { get; set; }
        public bool IsActive { get; set; } = true;
        public ISortItemLoadoutData? LoadoutData { get; set; }
        public string? Flags => null;
        public IReadOnlyList<string> Masters => [];
        public bool IsLocked => false;
        public SortItemLockReason? LockReason => null;
        public bool IsEnabled => true;
    }

    /// <summary>A toggleable variety that remembers which threads scanned it.</summary>
    private sealed class RecordingVariety : IToggleableSortOrderVariety
    {
        private readonly IReactiveSortItem[] _items = [new Row()];
        private readonly List<int> _callerThreadIds = [];

        public int[] CallerThreadIds { get { lock (_callerThreadIds) return _callerThreadIds.ToArray(); } }

        public SortOrderVarietyId SortOrderVarietyId { get; } = SortOrderVarietyId.From(Guid.NewGuid());
        public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;
        public IndexOverrideBehavior IndexOverrideBehavior => IndexOverrideBehavior.SmallerIndexWins;
        public SortOrderUiMetadata SortOrderUiMetadata { get; } = new()
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

        public Optional<SortOrderId> GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId> parentEntity, IDb? db = null) => SortOrderId.From(42UL);

        public IReadOnlyList<IReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db = null)
        {
            lock (_callerThreadIds) _callerThreadIds.Add(Environment.CurrentManagedThreadId);
            return _items;
        }

        public IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId)
            => _items.AsObservableChangeSet(item => item.Key);

        public Task SetEnabled(SortOrderId sortOrderId, ISortItemKey item, bool enabled, CancellationToken token = default) => Task.CompletedTask;

        public ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId, OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default) => throw new NotSupportedException();
        public Task MoveItems(SortOrderId sortOrderId, ISortItemKey[] itemsToMove, ISortItemKey dropTargetItem, TargetRelativePosition relativePosition, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task MoveItemDelta(SortOrderId sortOrderId, ISortItemKey sourceItem, int delta, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ClickingASwitchLooksTheRowUpOffTheUiThread()
    {
        var variety = new RecordingVariety();
        var vm = new LoadOrderViewModel(Provider, variety, LoadoutId.From(1));

        var uiThreadId = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await vm.ToggleEnabledCommand.Execute(RowKey);
            return Environment.CurrentManagedThreadId;
        });

        variety.CallerThreadIds.Should().NotBeEmpty("the click looks its row up");
        variety.CallerThreadIds.Should().NotContain(uiThreadId);
    }

    [Fact]
    public async Task OpeningThePageDoesNotScanTheSortOrderForTheLastIndex()
    {
        var variety = new RecordingVariety();

        var cache = await Dispatcher.UIThread.InvokeAsync(() => new LoadOrderDataProvider(Provider)
            .ObserveLoadOrder(variety, LoadoutId.From(1), R3.Observable.Return(ListSortDirection.Ascending))
            .AsObservableCache());
        await Eventually(() => cache.Count.Should().Be(1));

        variety.CallerThreadIds.Should().BeEmpty("the last index comes from the first change set the page already receives");
    }
}
