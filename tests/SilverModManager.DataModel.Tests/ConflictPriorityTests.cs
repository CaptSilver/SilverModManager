using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Pins how group priorities are reordered, read back by the conflict SQL and copied with a loadout.
/// </summary>
public class ConflictPriorityTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ConflictPriorityTests>(helper)
{
    private static readonly GamePath Conflict = new(LocationId.Game, "archive/pc/mod/conflict.archive");

    [Fact]
    public async Task WinnersMoveDirectlyAboveTheLoserAndTheRestKeepTheirOrder()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C", "D", "E");

        // C and E win against B: both end up directly above B, in their old relative order.
        await LoadoutManager.ResolveFileConflicts([groups["C"].priority, groups["E"].priority], groups["B"].priority);

        NamesByPriority(loadout.LoadoutId).Should().Equal("A", "B", "C", "E", "D");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL, 3UL, 4UL, 5UL);
    }

    [Fact]
    public async Task LosingAllConflictsMovesTheGroupsToTheBottom()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C", "D");

        await LoadoutManager.LoseAllFileConflicts([groups["C"].priority, groups["D"].priority]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("C", "D", "A", "B");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL, 3UL, 4UL);
    }

    [Fact]
    public async Task WinningAllConflictsMovesTheGroupsToTheTop()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C", "D");

        await LoadoutManager.WinAllFileConflicts([groups["A"].priority]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("B", "C", "D", "A");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL, 3UL, 4UL);
    }

    [Fact]
    public async Task RemovingAGroupLeavesDensePriorities()
    {
        var loadout = await CreateLoadout();
        // Sparse on purpose: removal has to close the gaps, not just drop the row.
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, [("A", 10UL), ("B", 20UL), ("C", 30UL), ("D", 40UL)]);

        await LoadoutManager.RemoveItems([groups["B"].group]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("A", "C", "D");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL, 3UL);
    }

    [Fact]
    public async Task PriorityGroupsOnlyCountTheirOwnLoadoutsWinnersAndLosers()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();

        // Same path in both loadouts. First: High wins. Second: the order is reversed, so Low wins.
        var firstGroups = await AddConflictingGroups(first.LoadoutId, lowPriority: 1, highPriority: 2);
        var secondGroups = await AddConflictingGroups(second.LoadoutId, lowPriority: 2, highPriority: 1);

        var rows = PriorityGroupRows(Connection.Db);

        Row(rows, firstGroups.high).Should().Be((first.LoadoutId.Value, 1L, 0L));
        Row(rows, firstGroups.low).Should().Be((first.LoadoutId.Value, 0L, 1L));
        Row(rows, secondGroups.low).Should().Be((second.LoadoutId.Value, 1L, 0L));
        Row(rows, secondGroups.high).Should().Be((second.LoadoutId.Value, 0L, 1L));
        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task PriorityGroupsReadTheDbTheyWereGiven()
    {
        var loadout = await CreateLoadout();
        var groups = await AddConflictingGroups(loadout.LoadoutId, lowPriority: 1, highPriority: 2);
        var before = Connection.Db;

        // Swap the winner after taking the snapshot.
        await LoadoutManager.WinAllFileConflicts([LoadoutItemGroupPriorityId.From(PriorityOf(Connection.Db, groups.low).Value)]);
        PriorityGroupRows(Connection.Db).Single(r => r.Id == PriorityOf(Connection.Db, groups.low))
            .Should().Match<(EntityId Id, EntityId Loadout, long Winning, long Losing)>(r => r.Winning == 1 && r.Losing == 0);

        var rows = PriorityGroupRows(before);

        rows.Single(r => r.Id == PriorityOf(before, groups.high)).Should().Match<(EntityId Id, EntityId Loadout, long Winning, long Losing)>(r => r.Winning == 1 && r.Losing == 0);
        rows.Single(r => r.Id == PriorityOf(before, groups.low)).Should().Match<(EntityId Id, EntityId Loadout, long Winning, long Losing)>(r => r.Winning == 0 && r.Losing == 1);
    }

    [Fact]
    public async Task ConflictingPathsAreReportedPerLoadout()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        await AddConflictingGroups(first.LoadoutId, lowPriority: 1, highPriority: 2);
        await AddConflictingGroups(second.LoadoutId, lowPriority: 1, highPriority: 2);

        var db = Connection.Db;
        var perLoadout = Connection.Query<(EntityId Loadout, long Conflicts)>(
            $"SELECT Loadout, len(Conflicts) FROM synchronizer.ConflictingPaths({db})"
        ).ToArray();

        perLoadout.Should().BeEquivalentTo(new[] { (first.LoadoutId.Value, 2L), (second.LoadoutId.Value, 2L) });
    }

    [Fact]
    public async Task ACopiedLoadoutKeepsItsGroupPrioritiesInOrder()
    {
        var loadout = await CreateLoadout();
        // Priority order differs from creation order so the copy can't get it right by accident.
        await AddGroupsWithPriorities(loadout.LoadoutId, [("A", 3UL), ("B", 1UL), ("C", 2UL)]);

        var copy = await LoadoutManager.CopyLoadout(loadout.LoadoutId);

        var copied = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, copy.LoadoutId).OrderBy(p => p.Priority).ToArray();
        copied.Select(p => p.Target.AsLoadoutItem().Name).Should().Equal("B", "C", "A");
        copied.Select(p => p.TargetId.Value).Should().NotIntersectWith(
            LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).Select(p => p.TargetId.Value),
            "the copy's priorities point at the copied groups, not the originals");
    }

    [Fact]
    public async Task TwoGroupsAddedInOneTransactionEachGetAPriorityRow()
    {
        var loadout = await CreateLoadout();

        // Installing several items into one loadout in one transaction (as ReplaceItems does) queues one
        // AddPriorityTxFunc per group; the transaction must not collapse them into one.
        using var tx = Connection.BeginTransaction();
        var first = AddEmptyGroup(tx, loadout.LoadoutId, "First");
        var second = AddEmptyGroup(tx, loadout.LoadoutId, "Second");
        tx.Add(new LoadoutManager.AddPriorityTxFunc(loadout.LoadoutId, first));
        tx.Add(new LoadoutManager.AddPriorityTxFunc(loadout.LoadoutId, second));
        await tx.Commit();

        NamesByPriority(loadout.LoadoutId).Should().BeEquivalentTo("First", "Second");
    }

    private async Task<Dictionary<string, (LoadoutItemGroupId group, LoadoutItemGroupPriorityId priority)>> AddGroupsWithPriorities(LoadoutId loadoutId, params string[] names)
        => await AddGroupsWithPriorities(loadoutId, names.Select((name, i) => (name, (ulong)i + 1)).ToArray());

    private async Task<Dictionary<string, (LoadoutItemGroupId group, LoadoutItemGroupPriorityId priority)>> AddGroupsWithPriorities(LoadoutId loadoutId, (string Name, ulong Priority)[] groups)
    {
        using var tx = Connection.BeginTransaction();
        var added = groups.Select(g =>
        {
            var group = AddEmptyGroup(tx, loadoutId, g.Name);
            var priority = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From(g.Priority) };
            return (g.Name, group, priority);
        }).ToArray();
        var result = await tx.Commit();

        return added.ToDictionary(
            a => a.Name,
            a => (LoadoutItemGroupId.From(result[a.group].Value), LoadoutItemGroupPriorityId.From(result[a.priority.Id].Value))
        );
    }

    private async Task<(LoadoutItemGroupId low, LoadoutItemGroupId high)> AddConflictingGroups(LoadoutId loadoutId, ulong lowPriority, ulong highPriority)
    {
        using var tx = Connection.BeginTransaction();
        var low = AddEmptyGroup(tx, loadoutId, "Low");
        var high = AddEmptyGroup(tx, loadoutId, "High");
        AddFile(tx, loadoutId, low, Conflict, "from low");
        AddFile(tx, loadoutId, high, Conflict, "from high");
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = low, Priority = ConflictPriority.From(lowPriority) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = high, Priority = ConflictPriority.From(highPriority) };
        var result = await tx.Commit();
        return (LoadoutItemGroupId.From(result[low].Value), LoadoutItemGroupId.From(result[high].Value));
    }

    private string[] NamesByPriority(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => p.Target.AsLoadoutItem().Name)
        .ToArray();

    private ulong[] PriorityValues(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => (ulong)p.Priority.Value)
        .ToArray();

    private static EntityId PriorityOf(IDb db, LoadoutItemGroupId group) => LoadoutItemGroupPriority.FindByTarget(db, group).Single().Id;

    private (EntityId Id, EntityId Loadout, long Winning, long Losing)[] PriorityGroupRows(IDb db) => Connection
        .Query<(EntityId Id, EntityId Loadout, long Winning, long Losing)>(
            $"SELECT Id, Loadout, len(WinningFiles), len(LosingFiles) FROM synchronizer.PriorityGroups({db})"
        ).ToArray();

    private (EntityId Loadout, long Winning, long Losing) Row((EntityId Id, EntityId Loadout, long Winning, long Losing)[] rows, LoadoutItemGroupId group)
    {
        var id = PriorityOf(Connection.Db, group);
        var row = rows.Single(r => r.Id == id);
        return (row.Loadout, row.Winning, row.Losing);
    }
}
