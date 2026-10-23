using System.ComponentModel;
using System.Collections.Concurrent;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using OneOf;

namespace NexusMods.Abstractions.Games.Tests;

/// <summary>
/// Sort orders follow the loadouts of one game. Two games that share a Nexus id (Skyrim SE and Skyrim VR)
/// must not see each other's loadouts or items.
/// </summary>
public class SortOrderManagerTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<SortOrderManagerTests>(helper)
{
    [Fact]
    public async Task LoadoutsOfAGameSharingTheNexusId_AreNotSortedByTheOtherGame()
    {
        var variety = new RecordingVariety();
        using var manager = new SortOrderManager(ServiceProvider);
        manager.RegisterSortOrderVarieties([variety], GameInstallation.GetGame());

        // The sibling goes first: whatever the manager does with it has happened by the time the manager
        // has dealt with the loadout that is its own.
        var sibling = await SeedLoadout(SiblingStubbedGame.GameId);
        await SeedFile(sibling);
        var own = await SeedLoadout(StubbedGame.GameId);
        await SeedFile(own);

        await variety.WaitForReconcile(own);

        variety.Created.Should().Contain(own);
        variety.Created.Should().NotContain(sibling, "the sibling is another game even though both have the same Nexus id");
        variety.Reconciled.Should().NotContain(sibling);
    }

    [Fact]
    public async Task LoadoutOnAnInstallationWithNoGameRecorded_IsLeftAlone()
    {
        var variety = new RecordingVariety();
        using var manager = new SortOrderManager(ServiceProvider);
        manager.RegisterSortOrderVarieties([variety], GameInstallation.GetGame());

        // A row the game-id migration could not place has no Game. It belongs to no game, and reading it must
        // not stop the manager from handling the loadouts that come after it.
        var unplaced = await SeedLoadout(game: null);
        await SeedFile(unplaced);
        var own = await SeedLoadout(StubbedGame.GameId);
        await SeedFile(own);

        await variety.WaitForReconcile(own);

        variety.Created.Should().NotContain(unplaced);
        variety.Reconciled.Should().NotContain(unplaced);
    }

    [Fact]
    public void GameWithoutVarieties_DoesNotSubscribe()
    {
        using var manager = new SortOrderManager(ServiceProvider);

        // A game without a Nexus id must be able to register an empty set.
        var act = () => manager.RegisterSortOrderVarieties([], new NoNexusIdStubbedGame(ServiceProvider));

        act.Should().NotThrow();
    }

    [Fact]
    public void GameWithoutNexusIdAndWithVarieties_Subscribes()
    {
        using var manager = new SortOrderManager(ServiceProvider);

        var act = () => manager.RegisterSortOrderVarieties([new RecordingVariety()], new NoNexusIdStubbedGame(ServiceProvider));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task EachGamesManagerKeepsOnlyItsOwnVarieties()
    {
        var varietyId = SortOrderVarietyId.From(Guid.NewGuid());
        var a = new RecordingVariety(varietyId);
        var b = new RecordingVariety(varietyId);
        var gameOne = GameInstallation.GetGame();
        var gameTwo = new NoNexusIdStubbedGame(ServiceProvider);

        using var managerOne = ServiceProvider.GetRequiredService<SortOrderManager>();
        using var managerTwo = ServiceProvider.GetRequiredService<SortOrderManager>();
        managerOne.Should().NotBeSameAs(managerTwo, "each game resolves a manager of its own");
        managerOne.RegisterSortOrderVarieties([a], gameOne);
        managerTwo.RegisterSortOrderVarieties([b], gameTwo);

        managerOne.GetSortOrderVarieties().Should().Equal(a);
        managerTwo.GetSortOrderVarieties().Should().Equal(b);

        var own = await SeedLoadout(StubbedGame.GameId);
        await SeedFile(own);
        await a.WaitForReconcile(own);
        b.Reconciled.Should().BeEmpty("the second manager's game has no loadouts here");
    }

    [Fact]
    public async Task Dispose_WaitsForAnUpdateThatIsStillRunning()
    {
        var variety = new BlockingVariety();
        var manager = new SortOrderManager(ServiceProvider);
        manager.RegisterSortOrderVarieties([variety], GameInstallation.GetGame());

        var update = manager.UpdateLoadOrders(LoadoutId.From(1)).AsTask();
        await variety.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var dispose = Task.Run(manager.Dispose);

        // A reconcile reads the database, so the manager must not report itself gone while one is running.
        dispose.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("the update has not finished");

        variety.Release.SetResult();
        await update.WaitAsync(TimeSpan.FromSeconds(20));
        await dispose.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task RegisteringTwice_ReplacesTheFirstSubscriptionInsteadOfAddingToIt()
    {
        var once = new RecordingVariety();
        var replaced = new RecordingVariety();
        using var control = new SortOrderManager(ServiceProvider);
        using var twice = new SortOrderManager(ServiceProvider);
        control.RegisterSortOrderVarieties([once], GameInstallation.GetGame());
        twice.RegisterSortOrderVarieties([new RecordingVariety()], GameInstallation.GetGame());
        twice.RegisterSortOrderVarieties([replaced], GameInstallation.GetGame());

        var own = await SeedLoadout(StubbedGame.GameId);
        await SeedFile(own);
        await once.WaitForReconcile(own);
        await replaced.WaitForReconcile(own);
        await Task.Delay(500);

        // A subscription left over from the first call would run every handler a second time.
        replaced.Created.Count(l => l == own).Should().Be(once.Created.Count(l => l == own));
        replaced.Reconciled.Count(l => l == own).Should().Be(once.Reconciled.Count(l => l == own));
    }

    [Fact]
    public async Task Dispose_StopsTheLiveSubscription()
    {
        var variety = new RecordingVariety();
        var manager = new SortOrderManager(ServiceProvider);
        manager.RegisterSortOrderVarieties([variety], GameInstallation.GetGame());
        manager.RegisterSortOrderVarieties([variety], GameInstallation.GetGame());
        manager.Dispose();

        var own = await SeedLoadout(StubbedGame.GameId);
        await SeedFile(own);
        await Task.Delay(500);

        variety.Created.Should().NotContain(own);
        variety.Reconciled.Should().NotContain(own);
    }

    [Fact]
    public async Task AGameInstalledFile_IsTrackedWithNoCollectionAndWakesTheManager()
    {
        var variety = new RecordingVariety();
        using var manager = new SortOrderManager(ServiceProvider);
        manager.RegisterSortOrderVarieties([variety], GameInstallation.GetGame());

        var own = await SeedLoadout(StubbedGame.GameId);
        await SeedFile(own);
        await variety.WaitForReconcile(own);
        var reconciledBefore = variety.Reconciled.Count(l => l == own);

        var file = await SeedGameInstalledFile(own);

        var tracked = SortOrderQueries.TrackLoadoutItemChanges(Connection, StubbedGame.GameId).AsObservableCache().Items
            .Single(row => row.ItemId == file.File);
        tracked.GroupId.Should().Be(file.Item, "the sort order row belongs to the item, not to the group");
        tracked.CollectionId.Should().Be(EntityId.From(0), "a game-installed group is not a collection group");
        tracked.LoadoutId.Should().Be(own.Value);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (variety.Reconciled.Count(l => l == own) <= reconciledBefore)
            await Task.Delay(25, cts.Token);
    }

    [Fact]
    public void AnItemDetailsImplementationWithoutALockReason_ReadsNull()
    {
        ISortItemDetails details = new PlainDetails();

        details.LockReason.Should().BeNull();
    }

    private sealed class PlainDetails : ISortItemDetails
    {
        public string? Flags => null;
        public IReadOnlyList<string> Masters => [];
        public bool IsLocked => false;
        public bool IsEnabled => true;
    }

    private async Task<(EntityId Group, EntityId Item, EntityId File)> SeedGameInstalledFile(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        _ = new GameInstalledGroup.New(tx, out var groupId)
        {
            InstalledForId = loadoutId,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "Installed by the game", LoadoutId = loadoutId },
            },
        };
        _ = new GameInstalledItem.New(tx, out var itemId)
        {
            Source = "SkyrimCreation",
            SourceId = "ccnew.esl",
            Confidence = "GameRecord",
            CanBeClaimed = true,
            DepotShipped = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, itemId) { Name = "A Creation", LoadoutId = loadoutId, ParentId = groupId },
            },
        };
        _ = new GameInstalledFile.New(tx, out var fileId)
        {
            Path = new GamePath(LocationId.Game, "Data/ccnew.esl").ToGamePathParentTuple(loadoutId),
            Hash = Hash.From(0xAB),
            Size = Size.From(10),
            LoadoutItem = new LoadoutItem.New(tx, fileId) { Name = "ccnew.esl", LoadoutId = loadoutId, ParentId = itemId },
        };
        var result = await tx.Commit();
        return (result[groupId], result[itemId], result[fileId]);
    }

    private async Task<LoadoutId> SeedLoadout(GameId? game)
    {
        using var tx = Connection.BeginTransaction();
        var install = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Unknown,
            Path = "/games/" + Guid.NewGuid(),
            GameId = StubbedGame.NexusModsGameId.Value,
            Name = game?.ToString() ?? "unplaced",
        };
        if (game is { } gameId) install.Game = gameId;
        var loadout = new Loadout.New(tx)
        {
            Name = Guid.NewGuid().ToString(),
            ShortName = "ZZZ",
            InstallationId = install,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return LoadoutId.From(result.Remap(loadout).Id);
    }

    private async Task SeedFile(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadoutId, "group");
        AddFile(tx, loadoutId, group, new GamePath(LocationId.Game, "file.txt"));
        await tx.Commit();
    }

    private sealed class RecordingVariety : ISortOrderVariety
    {
        private readonly ConcurrentDictionary<SortOrderId, LoadoutId> _orders = new();
        private int _next;

        public RecordingVariety() : this(SortOrderVarietyId.From(Guid.NewGuid())) { }
        public RecordingVariety(SortOrderVarietyId id) => SortOrderVarietyId = id;

        public ConcurrentBag<LoadoutId> Created { get; } = [];
        public ConcurrentBag<LoadoutId> Reconciled { get; } = [];

        public async Task WaitForReconcile(LoadoutId loadoutId)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!Reconciled.Contains(loadoutId))
                await Task.Delay(25, cts.Token);
        }

        public ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId, OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default)
        {
            Created.Add(loadoutId);
            var id = SortOrderId.From((ulong)Interlocked.Increment(ref _next));
            _orders[id] = loadoutId;
            return ValueTask.FromResult(id);
        }

        public ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default)
        {
            Reconciled.Add(_orders[sortOrderId]);
            return ValueTask.CompletedTask;
        }

        public Optional<SortOrderId> GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId> parentEntity, IDb? db = null) => Optional<SortOrderId>.None;
        public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default) => ValueTask.CompletedTask;

        public SortOrderVarietyId SortOrderVarietyId { get; }
        public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;
        public IndexOverrideBehavior IndexOverrideBehavior => throw new NotSupportedException();
        public SortOrderUiMetadata SortOrderUiMetadata => throw new NotSupportedException();
        public IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId) => throw new NotSupportedException();
        public IReadOnlyList<IReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db = null) => throw new NotSupportedException();
        public Task MoveItems(SortOrderId sortOrderId, ISortItemKey[] itemsToMove, ISortItemKey dropTargetItem, TargetRelativePosition relativePosition, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task MoveItemDelta(SortOrderId sortOrderId, ISortItemKey sourceItem, int delta, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
    }

    // Holds every reconcile until released, standing in for one that is busy in the database.
    private sealed class BlockingVariety : ISortOrderVariety
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SortOrderId> GetOrCreateSortOrderFor(LoadoutId loadoutId, OneOf<LoadoutId, CollectionGroupId> parentEntity, CancellationToken token = default) =>
            ValueTask.FromResult(SortOrderId.From(1));

        public async ValueTask ReconcileSortOrder(SortOrderId sortOrderId, IDb? db = null, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Release.Task;
        }

        public Optional<SortOrderId> GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId> parentEntity, IDb? db = null) => Optional<SortOrderId>.None;
        public ValueTask DeleteSortOrder(SortOrderId sortOrderId, CancellationToken token = default) => ValueTask.CompletedTask;

        public SortOrderVarietyId SortOrderVarietyId { get; } = SortOrderVarietyId.From(Guid.NewGuid());
        public ListSortDirection SortDirectionDefault => ListSortDirection.Ascending;
        public IndexOverrideBehavior IndexOverrideBehavior => throw new NotSupportedException();
        public SortOrderUiMetadata SortOrderUiMetadata => throw new NotSupportedException();
        public IObservable<IChangeSet<IReactiveSortItem, ISortItemKey>> GetSortOrderItemsChangeSet(SortOrderId sortOrderId) => throw new NotSupportedException();
        public IReadOnlyList<IReactiveSortItem> GetSortOrderItems(SortOrderId sortOrderId, IDb? db = null) => throw new NotSupportedException();
        public Task MoveItems(SortOrderId sortOrderId, ISortItemKey[] itemsToMove, ISortItemKey dropTargetItem, TargetRelativePosition relativePosition, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task MoveItemDelta(SortOrderId sortOrderId, ISortItemKey sourceItem, int delta, IDb? db = null, CancellationToken token = default) => throw new NotSupportedException();
    }
}
