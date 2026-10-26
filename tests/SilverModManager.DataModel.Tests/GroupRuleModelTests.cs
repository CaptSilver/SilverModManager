using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

using Manager = SilverModManager.DataModel.LoadoutManager;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Group rules and per-path winner pins: tracked with their loadout, copied with it and retracted with the
/// groups they name. Also the group factory that gives a new top-level group a priority row.
/// </summary>
public class GroupRuleModelTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GroupRuleModelTests>(helper)
{
    private static readonly GamePath Conflict = new(LocationId.Game, "archive/pc/mod/conflict.archive");

    [Fact]
    public async Task TrackedEntitiesIncludeTheRulesAndPinsOfTheirOwnLoadoutOnly()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        var (ruleId, pinId) = await AddRuleAndPin(loadout.LoadoutId);
        var (otherRuleId, otherPinId) = await AddRuleAndPin(other.LoadoutId);

        var owned = TrackedIds(loadout.Id);

        owned.Should().Contain([ruleId.Value, pinId.Value]);
        owned.Should().NotContain([otherRuleId.Value, otherPinId.Value]);
        TrackedIds(other.Id).Should().Contain([otherRuleId.Value, otherPinId.Value]);
    }

    [Fact]
    public async Task ACopiedLoadoutGetsItsOwnRulesAndPinsPointingAtTheCopiedGroups()
    {
        var loadout = await CreateLoadout();
        await AddRuleAndPin(loadout.LoadoutId);

        var copy = await LoadoutManager.CopyLoadout(loadout.LoadoutId);

        var db = Connection.Db;
        var copiedGroups = LoadoutItem.FindByLoadout(db, copy.LoadoutId).Select(i => i.Id).ToHashSet();
        var rule = LoadoutGroupRule.FindByLoadout(db, copy.LoadoutId).Should().ContainSingle().Subject;
        rule.Kind.Should().Be(GroupRuleKind.After);
        copiedGroups.Should().Contain([rule.SourceId.Value, rule.OtherId.Value]);

        var pin = LoadoutFileWinner.FindByLoadout(db, copy.LoadoutId).Should().ContainSingle().Subject;
        copiedGroups.Should().Contain(pin.WinnerGroupId.Value);
        pin.TargetPath.Item1.Should().Be(copy.Id, "the path's first element is the owning loadout, remapped to the copy");
        pin.TargetPath.Item2.Should().Be(Conflict.LocationId);
        pin.TargetPath.Item3.Should().Be(Conflict.Path);

        LoadoutGroupRule.FindByLoadout(db, loadout.LoadoutId).Should().ContainSingle("the original keeps its own row");
        LoadoutFileWinner.FindByLoadout(db, loadout.LoadoutId).Single().TargetPath.Item1.Should().Be(loadout.Id);
    }

    [Fact]
    public async Task RemovingAGroupRetractsEveryRuleAndPinNamingIt()
    {
        var loadout = await CreateLoadout();
        var a = await AddGroup(loadout.LoadoutId, "A");
        var b = await AddGroup(loadout.LoadoutId, "B");
        var c = await AddGroup(loadout.LoadoutId, "C");
        using (var tx = Connection.BeginTransaction())
        {
            AddRule(tx, loadout.LoadoutId, source: a, other: b);   // names A as Source
            AddRule(tx, loadout.LoadoutId, source: b, other: a);   // names A as Other
            AddRule(tx, loadout.LoadoutId, source: b, other: c);   // unrelated
            AddPin(tx, loadout.LoadoutId, a);                      // names A as WinnerGroup
            AddPin(tx, loadout.LoadoutId, c, new GamePath(LocationId.Game, "other.archive"));
            await tx.Commit();
        }

        await LoadoutManager.RemoveItems([a]);

        var db = Connection.Db;
        var rule = LoadoutGroupRule.FindByLoadout(db, loadout.LoadoutId).Should().ContainSingle().Subject;
        (rule.SourceId.Value, rule.OtherId.Value).Should().Be((b.Value, c.Value));
        LoadoutFileWinner.FindByLoadout(db, loadout.LoadoutId).Should().ContainSingle()
            .Which.WinnerGroupId.Value.Should().Be(c.Value);
    }

    [Fact]
    public async Task DeletingALoadoutRemovesItsRulesAndPins()
    {
        var loadout = await CreateLoadout();
        var (ruleId, pinId) = await AddRuleAndPin(loadout.LoadoutId);

        await LoadoutManager.DeleteLoadout(loadout.LoadoutId, deactivateIfActive: false);

        var db = Connection.Db;
        LoadoutGroupRule.Load(db, ruleId).IsValid().Should().BeFalse();
        LoadoutFileWinner.Load(db, pinId).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task AGroupFromTheFactoryGetsExactlyOnePriorityRow()
    {
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = Manager.AddEmptyGroup(tx, loadout.LoadoutId, "Made");
        var result = await tx.Commit();

        var db = Connection.Db;
        var id = LoadoutItemGroupId.From(result[group].Value);
        LoadoutItemGroupPriority.FindByTarget(db, id).Should().ContainSingle();
        var item = LoadoutItemGroup.Load(db, id);
        item.IsGroup.Should().BeTrue();
        item.AsLoadoutItem().Name.Should().Be("Made");
        item.AsLoadoutItem().LoadoutId.Should().Be(loadout.LoadoutId);
    }

    [Fact]
    public async Task TwoFactoryGroupsInOneTransactionGetTwoPriorityRowsAboveTheExistingOne()
    {
        var loadout = await CreateLoadout();
        await AddGroup(loadout.LoadoutId, "Existing", priority: 5UL);

        using var tx = Connection.BeginTransaction();
        Manager.AddEmptyGroup(tx, loadout.LoadoutId, "First");
        Manager.AddEmptyGroup(tx, loadout.LoadoutId, "Second");
        await tx.Commit();

        var rows = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).ToArray();
        rows.Should().HaveCount(3);
        rows.Where(r => r.Target.AsLoadoutItem().Name != "Existing").Should().OnlyContain(r => r.Priority.Value > 5UL);
    }

    [Fact]
    public async Task ANestedFactoryGroupGetsNoPriorityRow()
    {
        var loadout = await CreateLoadout();
        var parent = await AddGroup(loadout.LoadoutId, "Parent", priority: 1UL);

        using var tx = Connection.BeginTransaction();
        var child = Manager.AddEmptyGroup(tx, loadout.LoadoutId, "Child", parent);
        var result = await tx.Commit();

        var db = Connection.Db;
        var childId = LoadoutItemGroupId.From(result[child].Value);
        LoadoutItemGroupPriority.FindByTarget(db, childId).Should().BeEmpty();
        LoadoutItemGroup.Load(db, childId).AsLoadoutItem().ParentId.Value.Should().Be(parent.Value);
    }

    private HashSet<EntityId> TrackedIds(EntityId loadout) => Connection
        .Query<EntityId>($"SELECT Id FROM loadouts.TrackedEntitiesForLoadout({Connection.Db}, {loadout})")
        .ToHashSet();

    private async Task<LoadoutItemGroupId> AddGroup(LoadoutId loadoutId, string name, ulong? priority = null)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadoutId, name);
        AddFile(tx, loadoutId, group, Conflict, name);
        if (priority is { } p)
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From(p) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private async Task<(LoadoutGroupRuleId Rule, LoadoutFileWinnerId Pin)> AddRuleAndPin(LoadoutId loadoutId)
    {
        var a = await AddGroup(loadoutId, "A");
        var b = await AddGroup(loadoutId, "B");
        using var tx = Connection.BeginTransaction();
        var rule = AddRule(tx, loadoutId, a, b);
        var pin = AddPin(tx, loadoutId, a);
        var result = await tx.Commit();
        return (LoadoutGroupRuleId.From(result[rule].Value), LoadoutFileWinnerId.From(result[pin].Value));
    }

    private static EntityId AddRule(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId source, LoadoutItemGroupId other) =>
        new LoadoutGroupRule.New(tx)
        {
            LoadoutId = loadoutId,
            SourceId = source,
            OtherId = other,
            Kind = GroupRuleKind.After,
        }.Id;

    private static EntityId AddPin(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId winner, GamePath? path = null)
    {
        var p = path ?? Conflict;
        return new LoadoutFileWinner.New(tx)
        {
            LoadoutId = loadoutId,
            TargetPath = p.ToGamePathParentTuple(loadoutId.Value),
            WinnerGroupId = winner,
        }.Id;
    }
}
