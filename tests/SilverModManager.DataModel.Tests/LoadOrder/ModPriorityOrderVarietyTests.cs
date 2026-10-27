using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.LoadOrder;

/// <summary>
/// The Load Order page of a game with no sort order of its own is the loadout's priority rows, highest first.
/// </summary>
public class ModPriorityOrderVarietyTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ModPriorityOrderVarietyTests>(helper)
{
    private static readonly GamePath Conflict = new(LocationId.Game, "archive/pc/mod/conflict.archive");

    private ModPriorityOrderVariety Variety => new(Connection, LoadoutManager, NullLogger<ModPriorityOrderVariety>.Instance);

    private static LoadoutItemGroupPriorityId Row(ulong id) => LoadoutItemGroupPriorityId.From(EntityId.From(id));

    [Fact]
    public void PlanBeforeTargetMakesTheTargetTheLoser()
    {
        LoadoutItemGroupPriorityId[] highestFirst = [Row(1), Row(2), Row(3), Row(4)];

        var (moved, loser) = ModPriorityOrderVariety.Plan(highestFirst, [Row(4).Value], Row(2).Value, TargetRelativePosition.BeforeTarget);

        moved.Should().Equal(Row(4));
        loser.Should().Be(Row(2));
    }

    [Fact]
    public void PlanAfterTargetSkipsRowsBeingMovedWhenPickingTheLoser()
    {
        LoadoutItemGroupPriorityId[] highestFirst = [Row(1), Row(2), Row(3), Row(4)];

        // Row 3 sits right below the target but travels with the drag, so the row under it is the one to land over.
        var (moved, loser) = ModPriorityOrderVariety.Plan(highestFirst, [Row(3).Value, Row(1).Value], Row(2).Value, TargetRelativePosition.AfterTarget);

        // The moved rows keep the order they have in the list.
        moved.Should().Equal(Row(1), Row(3));
        loser.Should().Be(Row(4));
    }

    [Fact]
    public void PlanAfterTheLastRowGoesToTheBottom()
    {
        LoadoutItemGroupPriorityId[] highestFirst = [Row(1), Row(2), Row(3)];

        var (moved, loser) = ModPriorityOrderVariety.Plan(highestFirst, [Row(1).Value], Row(3).Value, TargetRelativePosition.AfterTarget);

        moved.Should().Equal(Row(1));
        loser.Should().BeNull();
    }

    [Fact]
    public void PlanForADropOnADraggedRowMovesNothing()
    {
        LoadoutItemGroupPriorityId[] highestFirst = [Row(1), Row(2), Row(3)];

        var (moved, _) = ModPriorityOrderVariety.Plan(highestFirst, [Row(1).Value, Row(2).Value], Row(2).Value, TargetRelativePosition.BeforeTarget);

        moved.Should().BeEmpty();
    }

    [Fact]
    public void PlanRefusesADragOutsideTheOrder()
    {
        var plan = () => ModPriorityOrderVariety.Plan([Row(1), Row(2)], [Row(9).Value], Row(1).Value, TargetRelativePosition.BeforeTarget);

        plan.Should().Throw<InvalidOperationException>().WithMessage("The mods you moved are no longer*");
    }

    [Fact]
    public void PlanRefusesATargetOutsideTheOrder()
    {
        var plan = () => ModPriorityOrderVariety.Plan([Row(1)], [Row(1).Value], Row(9).Value, TargetRelativePosition.BeforeTarget);

        plan.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ItemsListTheLoadoutsRowsLowestPriorityFirst()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C");

        var items = Variety.GetSortOrderItems(SortOrderIdOf(loadout.LoadoutId));

        items.Select(item => item.DisplayName).Should().Equal("A", "B", "C");
        items.Select(item => item.SortIndex).Should().Equal(0, 1, 2);
        items.Select(item => item.ModGroupId.Value).Should().Equal(groups["A"].group, groups["B"].group, groups["C"].group);
        items.Select(item => item.Key).Should().Equal(
            new SortItemKey<EntityId>(groups["A"].priority.Value),
            new SortItemKey<EntityId>(groups["B"].priority.Value),
            new SortItemKey<EntityId>(groups["C"].priority.Value));
    }

    [Fact]
    public async Task ADisabledModIsListedAsInactive()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(groups["A"].group.Value, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        var items = Variety.GetSortOrderItems(SortOrderIdOf(loadout.LoadoutId));

        items.Single(item => item.DisplayName == "A").IsActive.Should().BeFalse();
        items.Single(item => item.DisplayName == "B").IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ADragFlipsTheFileWinner()
    {
        var loadout = await CreateLoadout();
        var (low, high) = await AddConflictingGroups(loadout.LoadoutId);
        WinnerOf(low).Should().BeFalse();
        WinnerOf(high).Should().BeTrue();
        var variety = Variety;
        var sortOrder = SortOrderIdOf(loadout.LoadoutId);
        var items = variety.GetSortOrderItems(sortOrder);
        items.Select(item => item.ModGroupId.Value).Should().Equal(low, high);

        // High is last in the list; dropping it above Low hands the file to Low.
        await variety.MoveItems(sortOrder, [items[1].Key], items[0].Key, TargetRelativePosition.BeforeTarget);

        WinnerOf(low).Should().BeTrue();
        WinnerOf(high).Should().BeFalse();
        variety.GetSortOrderItems(sortOrder).Select(item => item.ModGroupId.Value).Should().Equal(high, low);
    }

    [Fact]
    public async Task MoveItemDeltaMovesARowThatManyPlaces()
    {
        var loadout = await CreateLoadout();
        await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C", "D");
        var variety = Variety;
        var sortOrder = SortOrderIdOf(loadout.LoadoutId);
        var items = variety.GetSortOrderItems(sortOrder);
        items.Select(item => item.DisplayName).Should().Equal("A", "B", "C", "D");

        await variety.MoveItemDelta(sortOrder, items[0].Key, 2);
        variety.GetSortOrderItems(sortOrder).Select(item => item.DisplayName).Should().Equal("B", "C", "A", "D");

        await variety.MoveItemDelta(sortOrder, variety.GetSortOrderItems(sortOrder)[2].Key, -2);
        variety.GetSortOrderItems(sortOrder).Select(item => item.DisplayName).Should().Equal("A", "B", "C", "D");
    }

    [Fact]
    public async Task MoveItemDeltaPastTheEndsStopsAtTheEnd()
    {
        var loadout = await CreateLoadout();
        await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C");
        var variety = Variety;
        var sortOrder = SortOrderIdOf(loadout.LoadoutId);
        var items = variety.GetSortOrderItems(sortOrder);

        await variety.MoveItemDelta(sortOrder, items[0].Key, 10);
        variety.GetSortOrderItems(sortOrder).Select(item => item.DisplayName).Should().Equal("B", "C", "A");
    }

    [Fact]
    public async Task AMoveARuleForbidsIsRefusedWithTheRulesText()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "a", "b");
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, groups["a"].group, groups["b"].group, GroupRuleKind.After);
        var variety = Variety;
        var sortOrder = SortOrderIdOf(loadout.LoadoutId);
        var items = variety.GetSortOrderItems(sortOrder);
        items.Select(item => item.DisplayName).Should().Equal("b", "a");

        // Dropping b below a in the list puts it above a in priority.
        var move = () => variety.MoveItems(sortOrder, [items[0].Key], items[1].Key, TargetRelativePosition.AfterTarget);

        (await move.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("That move would break the rule 'a wins over b'.");
        variety.GetSortOrderItems(sortOrder).Select(item => item.DisplayName).Should().Equal("b", "a");
    }

    [Fact]
    public async Task AMoveOnFileConflictsShowsUpHere()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C");

        await LoadoutManager.ResolveFileConflicts([groups["A"].priority], groups["C"].priority);

        Variety.GetSortOrderItems(SortOrderIdOf(loadout.LoadoutId)).Select(item => item.DisplayName)
            .Should().Equal("B", "C", "A");
    }

    [Fact]
    public async Task AStaleDragIsRefusedWithReadableText()
    {
        var loadout = await CreateLoadout();
        await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B");
        var variety = Variety;
        var sortOrder = SortOrderIdOf(loadout.LoadoutId);
        var items = variety.GetSortOrderItems(sortOrder);

        var gone = new SortItemKey<EntityId>(EntityId.From(0xDEAD));
        var staleDrag = () => variety.MoveItems(sortOrder, [gone], items[0].Key, TargetRelativePosition.BeforeTarget);
        var staleTarget = () => variety.MoveItems(sortOrder, [items[0].Key], gone, TargetRelativePosition.BeforeTarget);
        var staleDelta = () => variety.MoveItemDelta(sortOrder, gone, 1);

        (await staleDrag.Should().ThrowAsync<InvalidOperationException>()).WithMessage("The mods you moved are no longer in the load order.*");
        (await staleTarget.Should().ThrowAsync<InvalidOperationException>()).WithMessage("The mod you dropped on is no longer in the load order.*");
        (await staleDelta.Should().ThrowAsync<InvalidOperationException>()).WithMessage("The mod you moved is no longer in the load order.*");
    }

    [Fact]
    public async Task AModInADisabledCollectionIsListedAsInactive()
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var collection = AddEmptyGroup(tx, loadout.LoadoutId, "Pack");
        var child = new LoadoutItemGroup.New(tx, out var childId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, childId) { LoadoutId = loadout.LoadoutId, Name = "Packed", ParentId = collection.Value },
        };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = child.Id, Priority = ConflictPriority.From(1) };
        tx.Add(collection.Value, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();

        Variety.GetSortOrderItems(SortOrderIdOf(loadout.LoadoutId)).Should().ContainSingle()
            .Which.IsActive.Should().BeFalse("the mod's own switch is on but its collection is off");
    }

    [Fact]
    public async Task ARenameAndAnEnableToggleReachTheChangeSet()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B");
        using var subscription = Variety.GetSortOrderItemsChangeSet(SortOrderIdOf(loadout.LoadoutId)).Bind(out var bound).Subscribe();
        bound.Select(item => item.DisplayName).Should().BeEquivalentTo("A", "B");

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(groups["A"].group.Value, LoadoutItem.Name, "Renamed");
            await tx.Commit();
        }
        await Eventually(() => bound.Select(item => item.DisplayName).Should().BeEquivalentTo("Renamed", "B"));

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(groups["B"].group.Value, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Eventually(() => bound.Single(item => item.DisplayName == "B").IsActive.Should().BeFalse());
    }

    [Fact]
    public async Task ABurstOfCommitsIsReadAsOneChange()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A");
        var notifications = 0;
        using var subscription = Variety.GetSortOrderItemsChangeSet(SortOrderIdOf(loadout.LoadoutId)).Subscribe(_ => Interlocked.Increment(ref notifications));
        notifications.Should().Be(1, "the subscription starts with the current order");

        for (var i = 0; i < 10; i++)
        {
            using var tx = Connection.BeginTransaction();
            tx.Add(groups["A"].group.Value, LoadoutItem.Name, $"Name {i}");
            await tx.Commit();
        }

        await Eventually(() => Variety.GetSortOrderItems(SortOrderIdOf(loadout.LoadoutId)).Single().DisplayName.Should().Be("Name 9"));
        await Task.Delay(300);
        notifications.Should().BeLessThan(5, "ten commits in a row are one read, not ten");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, exception));
        }
    }

    [Fact]
    public async Task AFailedReadIsLoggedAndTheNextRevisionStillUpdatesThePage()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A");
        var logger = new CapturingLogger<ModPriorityOrderVariety>();
        var failing = false;
        var variety = new ModPriorityOrderVariety(Connection, LoadoutManager, logger)
        {
            ReadItems = (db, id) => failing ? throw new InvalidOperationException("unreadable row") : Variety.GetSortOrderItems(SortOrderIdOf(id), db).ToList(),
        };
        var escaped = new List<Exception>();
        UnhandledExceptionEventHandler onUnhandled = (_, e) => escaped.Add((Exception)e.ExceptionObject);
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        try
        {
            using var subscription = variety.GetSortOrderItemsChangeSet(SortOrderIdOf(loadout.LoadoutId)).Bind(out var bound).Subscribe();

            failing = true;
            using (var tx = Connection.BeginTransaction())
            {
                tx.Add(groups["A"].group.Value, LoadoutItem.Name, "Broken");
                await tx.Commit();
            }
            await Eventually(() => logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Error is InvalidOperationException));
            bound.Select(item => item.DisplayName).Should().Equal("A");

            failing = false;
            using (var tx = Connection.BeginTransaction())
            {
                tx.Add(groups["A"].group.Value, LoadoutItem.Name, "Fixed");
                await tx.Commit();
            }
            await Eventually(() => bound.Select(item => item.DisplayName).Should().Equal("Fixed"));
            escaped.Should().BeEmpty();
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
        }
    }

    [Fact]
    public async Task ACommitToAnotherLoadoutIsNotAChangeHere()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        await AddGroupsWithPriorities(loadout.LoadoutId, "A");
        var notifications = 0;
        using var subscription = Variety.GetSortOrderItemsChangeSet(SortOrderIdOf(loadout.LoadoutId)).Subscribe(_ => Interlocked.Increment(ref notifications));
        notifications.Should().Be(1);

        await AddGroupsWithPriorities(other.LoadoutId, "X", "Y");
        await Task.Delay(400);

        notifications.Should().Be(1, "the other loadout's mods are not on this page");
    }

    [Fact]
    public async Task TheChangeSetFollowsAMove()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C");
        using var subscription = Variety.GetSortOrderItemsChangeSet(SortOrderIdOf(loadout.LoadoutId))
            .Bind(out var bound)
            .Subscribe();
        bound.OrderBy(item => item.SortIndex).Select(item => item.DisplayName).Should().Equal("A", "B", "C");

        await LoadoutManager.ResolveFileConflicts([groups["A"].priority], groups["C"].priority);

        await Eventually(() => bound.OrderBy(item => item.SortIndex).Select(item => item.DisplayName).Should().Equal("B", "C", "A"));
    }

    [Fact]
    public async Task TheLoadoutsOwnIdIsTheHandleAndOnlyLoadoutsHaveOne()
    {
        var loadout = await CreateLoadout();
        var variety = Variety;

        variety.GetSortOrderIdFor(loadout.LoadoutId).Value.Should().Be(SortOrderIdOf(loadout.LoadoutId));
        (await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId)).Should().Be(SortOrderIdOf(loadout.LoadoutId));
        variety.GetSortOrderIdFor(CollectionGroupId.From(EntityId.From(1))).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ReconcileAndDeleteLeaveTheOrderAlone()
    {
        var loadout = await CreateLoadout();
        await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B");
        var variety = Variety;
        var sortOrder = SortOrderIdOf(loadout.LoadoutId);

        await variety.ReconcileSortOrder(sortOrder);
        await variety.DeleteSortOrder(sortOrder);

        variety.GetSortOrderItems(sortOrder).Select(item => item.DisplayName).Should().Equal("A", "B");
    }

    [Fact]
    public void TheWinnerIsAtTheBottomLikeOnFileConflicts()
    {
        // Rules > File Conflicts says mods lower in the list win; this page lists the same rows, so it must agree.
        Variety.IndexOverrideBehavior.Should().Be(IndexOverrideBehavior.GreaterIndexWins);
        Variety.SortDirectionDefault.Should().Be(System.ComponentModel.ListSortDirection.Ascending);
    }

    private static async Task Eventually(Action assertion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    private static SortOrderId SortOrderIdOf(LoadoutId loadoutId) => SortOrderId.From(loadoutId.Value);

    private bool WinnerOf(LoadoutItemGroupId group)
    {
        var id = LoadoutItemGroupPriority.FindByTarget(Connection.Db, group).Single().Id;
        return Connection.Query<(EntityId Id, long Winning)>($"SELECT Id, len(WinningFiles) FROM synchronizer.PriorityGroups({Connection.Db})")
            .Single(row => row.Id == id).Winning > 0;
    }

    private async Task<Dictionary<string, (LoadoutItemGroupId group, LoadoutItemGroupPriorityId priority)>> AddGroupsWithPriorities(LoadoutId loadoutId, params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        var added = names.Select((name, i) =>
        {
            var group = AddEmptyGroup(tx, loadoutId, name);
            var priority = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)i + 1) };
            return (name, group, priority);
        }).ToArray();
        var result = await tx.Commit();

        return added.ToDictionary(
            a => a.name,
            a => (LoadoutItemGroupId.From(result[a.group].Value), LoadoutItemGroupPriorityId.From(result[a.priority.Id].Value))
        );
    }

    private async Task<(LoadoutItemGroupId low, LoadoutItemGroupId high)> AddConflictingGroups(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        var low = AddEmptyGroup(tx, loadoutId, "Low");
        var high = AddEmptyGroup(tx, loadoutId, "High");
        AddFile(tx, loadoutId, low, Conflict, "from low");
        AddFile(tx, loadoutId, high, Conflict, "from high");
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = low, Priority = ConflictPriority.From(1) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = high, Priority = ConflictPriority.From(2) };
        var result = await tx.Commit();
        return (LoadoutItemGroupId.From(result[low].Value), LoadoutItemGroupId.From(result[high].Value));
    }
}
