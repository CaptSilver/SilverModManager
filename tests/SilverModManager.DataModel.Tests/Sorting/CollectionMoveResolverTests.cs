using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Sorting;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

using Manager = SilverModManager.DataModel.LoadoutManager;

namespace SilverModManager.DataModel.Tests.Sorting;

/// <summary>
/// A transaction that re-parents mods into another collection cannot show its own Parent writes to the resolver
/// queued beside them, so the move is handed over in <see cref="RuleDelta.MovedGroups"/>.
/// </summary>
public class CollectionMoveResolverTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionMoveResolverTests>(helper)
{
    [Fact]
    public async Task AMovedGroupIsListedUnderItsNewCollectionAndNotItsOldOne()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        var moves = ImmutableDictionary<LoadoutItemGroupId, LoadoutItemGroupId>.Empty.Add(f.FirstOfA, f.B);

        var members = Manager.CollectionMembers(Connection.Db, loadout.LoadoutId, moves);

        members[f.B].Should().Equal(f.FirstOfA, f.OfB);
        members[f.A].Should().Equal(f.LastOfA);
    }

    [Fact]
    public async Task WithoutMovesTheMembersAreTheStoredParents()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);

        var members = Manager.CollectionMembers(Connection.Db, loadout.LoadoutId);

        members[f.A].Should().Equal(f.FirstOfA, f.LastOfA);
        members[f.B].Should().Equal(f.OfB);
    }

    [Fact]
    public async Task AGroupMovedIntoACollectionThatHasNoMembersYetStartsIt()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        var empty = await AddCollection(loadout.LoadoutId, "Empty");
        var moves = ImmutableDictionary<LoadoutItemGroupId, LoadoutItemGroupId>.Empty.Add(f.FirstOfA, empty);

        Manager.CollectionMembers(Connection.Db, loadout.LoadoutId, moves)[empty].Should().Equal(f.FirstOfA);
    }

    [Fact]
    public async Task ARuleOnTheTargetCollectionLiftsAMovedModAboveTheModItMustFollowInTheSameCommit()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        await AddRule(loadout.LoadoutId, f.B, f.X, GroupRuleKind.After);
        NamesByPriority(loadout.LoadoutId).Should().Equal("A first", "X", "B only", "A last");

        await Reparent(loadout.LoadoutId, f.FirstOfA, f.B, withMoves: true);

        NamesByPriority(loadout.LoadoutId).Should().Equal("X", "A first", "B only", "A last");
    }

    [Fact]
    public async Task WithoutTheMoveInTheDeltaTheResolverStillSeesTheOldParent()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        await AddRule(loadout.LoadoutId, f.B, f.X, GroupRuleKind.After);

        await Reparent(loadout.LoadoutId, f.FirstOfA, f.B, withMoves: false);

        NamesByPriority(loadout.LoadoutId).Should().Equal("A first", "X", "B only", "A last");
    }

    [Fact]
    public async Task ARuleOnTheOldCollectionNoLongerBindsAModThatMovedOut()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        // A's members have to follow X, and "A first" does not: it is stored below X.
        await AddRule(loadout.LoadoutId, f.A, f.X, GroupRuleKind.After);

        await Reparent(loadout.LoadoutId, f.FirstOfA, f.B, withMoves: true);

        NamesByPriority(loadout.LoadoutId).Should().Equal("A first", "X", "B only", "A last");
    }

    [Fact]
    public async Task WithNoCollectionRuleAMoveLeavesEveryPriorityAlone()
    {
        var loadout = await CreateLoadout();
        var f = await Seed(loadout.LoadoutId);
        var rowOf = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).ToDictionary(static p => p.TargetId, static p => p.Id);

        await Reparent(loadout.LoadoutId, f.FirstOfA, f.B, withMoves: true);

        // The resolver renumbers the rows densely, so the order and the rows themselves are what has to stay.
        NamesByPriority(loadout.LoadoutId).Should().Equal("A first", "X", "B only", "A last");
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).ToDictionary(static p => p.TargetId, static p => p.Id).Should().Equal(rowOf);
        LoadoutItemGroup.Load(Connection.Db, f.FirstOfA).AsLoadoutItem().ParentId.Should().Be(f.B);
    }

    private async Task Reparent(LoadoutId loadoutId, LoadoutItemGroupId group, LoadoutItemGroupId target, bool withMoves)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(group, LoadoutItem.Parent, target);
        tx.Add(new Manager.ApplyGroupRulesTxFunc(loadoutId, new RuleDelta
        {
            MovedGroups = withMoves ? ImmutableDictionary<LoadoutItemGroupId, LoadoutItemGroupId>.Empty.Add(group, target) : ImmutableDictionary<LoadoutItemGroupId, LoadoutItemGroupId>.Empty,
        }));
        await tx.Commit();
    }

    private async Task AddRule(LoadoutId loadoutId, LoadoutItemGroupId source, LoadoutItemGroupId other, GroupRuleKind kind)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutGroupRule.New(tx) { LoadoutId = loadoutId, SourceId = source, OtherId = other, Kind = kind };
        await tx.Commit();
    }

    private sealed record Fixture(LoadoutItemGroupId A, LoadoutItemGroupId B, LoadoutItemGroupId FirstOfA, LoadoutItemGroupId X, LoadoutItemGroupId OfB, LoadoutItemGroupId LastOfA);

    /// <summary>Priority order, lowest first: A first, X, B only, A last. A and B are collections.</summary>
    private async Task<Fixture> Seed(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        LoadoutItemGroupId NewCollection(string name)
        {
            _ = new CollectionGroup.New(tx, out var id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadoutId },
                },
            };
            return LoadoutItemGroupId.From(id.Value);
        }

        var a = NewCollection("A");
        var b = NewCollection("B");
        var mods = new (string Name, LoadoutItemGroupId? Parent)[] { ("A first", a), ("X", null), ("B only", b), ("A last", a) };
        var added = mods.Select((m, i) =>
        {
            // Written by hand: AddEmptyGroup queues its own priority row for a group with no parent.
            var id = tx.TempId();
            var item = new LoadoutItem.New(tx, id) { Name = m.Name, LoadoutId = loadoutId };
            if (m.Parent is { } parent) item.ParentId = parent;
            _ = new LoadoutItemGroup.New(tx, id) { IsGroup = true, LoadoutItem = item };
            var group = LoadoutItemGroupId.From(id.Value);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)(i + 1) * 10) };
            return group;
        }).ToArray();
        var result = await tx.Commit();

        LoadoutItemGroupId Map(LoadoutItemGroupId id) => LoadoutItemGroupId.From(result[id.Value].Value);
        return new Fixture(Map(a), Map(b), Map(added[0]), Map(added[1]), Map(added[2]), Map(added[3]));
    }

    private async Task<LoadoutItemGroupId> AddCollection(LoadoutId loadoutId, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadoutId },
            },
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    private string[] NamesByPriority(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => p.Target.AsLoadoutItem().Name)
        .ToArray();
}
