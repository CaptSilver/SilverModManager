using System.ComponentModel;
using System.Collections.Concurrent;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
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
