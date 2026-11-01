using DynamicData;
using FluentAssertions;
using SilverModManager.MnemonicDB.Abstractions.DatomIterators;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.DataModel.Tests.Library;

public class LibraryDestinationsTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LibraryDestinationsTests>(helper)
{
    private static readonly GameId GameA = GameId.From("destinations-test-a");
    private static readonly GameId GameB = GameId.From("destinations-test-b");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private async Task<EntityId> AddItem(Destination destination)
    {
        using var tx = Connection.BeginTransaction();
        var temp = tx.TempId();
        _ = new LibraryItem.New(tx, temp) { Name = Guid.NewGuid().ToString() };
        LibraryDestinations.Write(tx, temp, destination);
        var result = await tx.Commit();
        return result[temp];
    }

    private async Task Run(Action<ITransaction> edit)
    {
        using var tx = Connection.BeginTransaction();
        edit(tx);
        await tx.Commit();
    }

    private LibraryItem.ReadOnly Item(EntityId id) => LibraryItem.Load(Connection.Db, id);

    private static Destination Both() => new(new HashSet<GameId> { GameA, GameB }, Shared: false);

    /// <summary>Everything a slice has emitted, and what it holds now.</summary>
    private sealed class Recording : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<Change<Datom, EntityId>> _changes = [];
        private readonly HashSet<EntityId> _held = [];
        private readonly IDisposable _subscription;

        public Recording(IObservable<IChangeSet<Datom, EntityId>> source)
        {
            _subscription = source.Subscribe(set =>
            {
                lock (_lock)
                {
                    foreach (var change in set)
                    {
                        _changes.Add(change);
                        if (change.Reason == ChangeReason.Remove) _held.Remove(change.Key);
                        else _held.Add(change.Key);
                    }
                }
            });
        }

        public IReadOnlySet<EntityId> Held { get { lock (_lock) return _held.ToHashSet(); } }
        public IReadOnlyList<Change<Datom, EntityId>> Changes { get { lock (_lock) return _changes.ToArray(); } }
        public void Dispose() => _subscription.Dispose();
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The slice never reached the expected state.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Write_ThenGamesOf_RoundTripsASet()
    {
        var id = await AddItem(Both());

        LibraryDestinations.GamesOf(Item(id)).Should().BeEquivalentTo([GameA, GameB]);
        Item(id).Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
        Item(id).Contains(LibraryItem.DestinationsShared).Should().BeFalse();
    }

    [Fact]
    public async Task Write_SharedDestination_SetsTheSharedMarker()
    {
        var id = await AddItem(new Destination(new HashSet<GameId> { GameA, GameB }, Shared: true));

        Item(id).Contains(LibraryItem.DestinationsShared).Should().BeTrue();
    }

    [Fact]
    public async Task Write_UnknownDestination_SetsTheUnknownMarkerAndNoGames()
    {
        var id = await AddItem(Destination.Unknown);

        Item(id).Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
        LibraryDestinations.GamesOf(Item(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Add_ClearsUnknown()
    {
        var id = await AddItem(Destination.Unknown);

        await Run(tx => LibraryDestinations.Add(tx, id, Destination.Of(GameA)));

        Item(id).Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
        LibraryDestinations.GamesOf(Item(id)).Should().BeEquivalentTo([GameA]);
    }

    [Fact]
    public async Task Add_UnionsIntoTheExistingSet()
    {
        var id = await AddItem(Destination.Of(GameA));

        await Run(tx => LibraryDestinations.Add(tx, id, Destination.Of(GameB)));

        LibraryDestinations.GamesOf(Item(id)).Should().BeEquivalentTo([GameA, GameB]);
    }

    [Fact]
    public async Task Add_SharedDestination_OnAnItemWithGames_DoesNotSetShared()
    {
        var id = await AddItem(Destination.Of(GameA));

        await Run(tx => LibraryDestinations.Add(tx, id, new Destination(new HashSet<GameId> { GameB }, Shared: true)));

        Item(id).Contains(LibraryItem.DestinationsShared).Should().BeFalse();
    }

    [Fact]
    public async Task Add_SharedDestination_OnAnItemWithoutGames_SetsShared_AndNeverClearsIt()
    {
        var id = await AddItem(Destination.Unknown);

        await Run(tx => LibraryDestinations.Add(tx, id, new Destination(new HashSet<GameId> { GameA, GameB }, Shared: true)));
        Item(id).Contains(LibraryItem.DestinationsShared).Should().BeTrue();

        await Run(tx => LibraryDestinations.Add(tx, id, Destination.Of(GameA)));
        Item(id).Contains(LibraryItem.DestinationsShared).Should().BeTrue();
    }

    [Fact]
    public async Task Remove_OneOfTwoGames_KeepsTheOther_AndIsNotUnknown()
    {
        var id = await AddItem(Both());

        await Run(tx => LibraryDestinations.Remove(tx, id, GameA));

        LibraryDestinations.GamesOf(Item(id)).Should().BeEquivalentTo([GameB]);
        Item(id).Contains(LibraryItem.DestinationUnknown).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_LastGame_SetsUnknown()
    {
        var id = await AddItem(Destination.Of(GameA));

        await Run(tx => LibraryDestinations.Remove(tx, id, GameA));

        LibraryDestinations.GamesOf(Item(id)).Should().BeEmpty();
        Item(id).Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
    }

    [Fact]
    public async Task Remove_TwoConcurrentRemovals_OfBothGames_LeavesUnknown()
    {
        var id = await AddItem(Both());

        // Both transactions are built before either commits, so both would see two games in a snapshot.
        using var removeA = Connection.BeginTransaction();
        using var removeB = Connection.BeginTransaction();
        LibraryDestinations.Remove(removeA, id, GameA);
        LibraryDestinations.Remove(removeB, id, GameB);
        await removeA.Commit();
        await removeB.Commit();

        LibraryDestinations.GamesOf(Item(id)).Should().BeEmpty();
        Item(id).Contains(LibraryItem.DestinationUnknown).Should().BeTrue();
    }

    [Fact]
    public async Task ObserveItemsFor_EmitsOnlyThatGame()
    {
        var forA = await AddItem(Destination.Of(GameA));
        var forB = await AddItem(Destination.Of(GameB));
        var forBoth = await AddItem(Both());

        using var recording = new Recording(LibraryDestinations.ObserveItemsFor(Connection, GameA));
        await Eventually(() => recording.Held.Count >= 2);
        var forBLater = await AddItem(Destination.Of(GameB));
        var forALater = await AddItem(Destination.Of(GameA));
        await Eventually(() => recording.Held.Contains(forALater));

        recording.Held.Should().BeEquivalentTo([forA, forBoth, forALater]);
        recording.Changes.Select(c => c.Key).Should().NotContain([forB, forBLater]);
    }

    [Fact]
    public async Task ItemsFor_ReadsTheSameSliceFromASnapshot()
    {
        var forA = await AddItem(Destination.Of(GameA));
        await AddItem(Destination.Of(GameB));

        LibraryDestinations.ItemsFor(Connection.Db, GameA).Select(i => i.Id).Should().BeEquivalentTo([forA]);
    }

    [Fact]
    public async Task ObserveShownFor_IncludesUnassigned()
    {
        var forA = await AddItem(Destination.Of(GameA));
        var forB = await AddItem(Destination.Of(GameB));
        var unknown = await AddItem(Destination.Unknown);

        using var recording = new Recording(LibraryDestinations.ObserveShownFor(Connection, GameA));
        await Eventually(() => recording.Held.Count >= 2);

        recording.Held.Should().BeEquivalentTo([forA, unknown]);
        recording.Changes.Select(c => c.Key).Should().NotContain(forB);
        LibraryDestinations.Unassigned(Connection.Db).Select(i => i.Id).Should().BeEquivalentTo([unknown]);
    }

    [Fact]
    public async Task ObserveShownFor_MovingFromUnassignedToTheGame_NeverRemovesTheRow()
    {
        var id = await AddItem(Destination.Unknown);
        using var recording = new Recording(LibraryDestinations.ObserveShownFor(Connection, GameA));
        await Eventually(() => recording.Held.Contains(id));
        using var assigned = new Recording(LibraryDestinations.ObserveItemsFor(Connection, GameA));

        await Run(tx => LibraryDestinations.Add(tx, id, Destination.Of(GameA)));
        await Eventually(() => assigned.Held.Contains(id));
        await Task.Delay(250);

        recording.Changes.Should().NotContain(c => c.Reason == ChangeReason.Remove && c.Key == id);
        recording.Held.Should().Contain(id);
    }

    [Fact]
    public async Task Partition_SplitsOnlyThisGameFromShared()
    {
        var only = await AddItem(Destination.Of(GameA));
        var shared = await AddItem(Both());

        var items = LibraryDestinations.ItemsFor(Connection.Db, GameA).ToArray();
        var (onlyThisGame, sharedWithOthers) = LibraryDestinations.Partition(Connection.Db, items, GameA);

        onlyThisGame.Select(i => i.Id).Should().BeEquivalentTo([only]);
        sharedWithOthers.Select(i => i.Id).Should().BeEquivalentTo([shared]);
    }

    [Fact]
    public void Destination_Equality_ComparesTheSets()
    {
        var one = new Destination(new HashSet<GameId> { GameA, GameB }, Shared: false);
        var two = new Destination(new HashSet<GameId> { GameB, GameA }, Shared: false);

        one.Should().Be(two);
        one.GetHashCode().Should().Be(two.GetHashCode());
        one.Should().NotBe(new Destination(new HashSet<GameId> { GameA, GameB }, Shared: true));
        one.Should().NotBe(Destination.Of(GameA));
        Destination.Unknown.IsUnknown.Should().BeTrue();
        Destination.Of(GameA).IsUnknown.Should().BeFalse();
    }
}
