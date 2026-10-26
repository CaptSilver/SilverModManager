using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Sorting;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;

using Manager = SilverModManager.DataModel.LoadoutManager;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Group rules turned into priority rows: through the manager's rule API and through the transaction function
/// it adds, which only ever sees the database from before its transaction.
/// </summary>
public class GroupRuleResolutionTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GroupRuleResolutionTests>(helper)
{
    [Fact]
    public async Task AddingARuleMovesTheGroupsAndWritesTheRule()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B", "C");

        // A should win over C, so it has to end up after it.
        var result = await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["A"], g["C"], GroupRuleKind.After);

        result.IsCycle.Should().BeFalse();
        NamesByPriority(loadout.LoadoutId).Should().Equal("B", "C", "A");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL, 3UL);
        var rule = LoadoutGroupRule.Load(Connection.Db, result.RuleId);
        (rule.SourceId.Value, rule.OtherId.Value, rule.Kind).Should().Be((g["A"].Value, g["C"].Value, GroupRuleKind.After));
        rule.LoadoutId.Should().Be(loadout.LoadoutId);
    }

    [Fact]
    public async Task ARuleThatContradictsAStoredOneIsRefusedAndWritesNothing()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B", "C");
        (await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["A"], g["B"], GroupRuleKind.After)).IsCycle.Should().BeFalse();
        var before = NamesByPriority(loadout.LoadoutId);

        var result = await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["B"], g["A"], GroupRuleKind.After);

        result.IsCycle.Should().BeTrue();
        new[] { result.Cycle.First.Value, result.Cycle.Second.Value }.Should().BeEquivalentTo([g["A"].Value, g["B"].Value]);
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle();
        NamesByPriority(loadout.LoadoutId).Should().Equal(before);
    }

    [Fact]
    public async Task ARuleNamingAGroupOfAnotherLoadoutThrowsAndWritesNothing()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        var a = await AddGroups(first.LoadoutId, "A", "B");
        var b = await AddGroups(second.LoadoutId, "X");

        var act = async () => await LoadoutManager.AddGroupRule(first.LoadoutId, a["A"], b["X"], GroupRuleKind.After);

        await act.Should().ThrowAsync<ArgumentException>();
        LoadoutGroupRule.FindByLoadout(Connection.Db, first.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingARuleDeletesTheRowAndLeavesThePriorityRowsUntouched()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B", "C");
        var added = await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["A"], g["C"], GroupRuleKind.After);
        var rowsBefore = PriorityRows(loadout.LoadoutId);

        await LoadoutManager.RemoveGroupRule(added.RuleId);

        LoadoutGroupRule.Load(Connection.Db, added.RuleId).IsValid().Should().BeFalse();
        PriorityRows(loadout.LoadoutId).Should().Equal(rowsBefore);
    }

    [Fact]
    public async Task ResolversForTwoLoadoutsInOneTransactionBothRun()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        var a = await AddGroups(first.LoadoutId, "A", "B", "C");
        var b = await AddGroups(second.LoadoutId, "A", "B", "C");

        using var tx = Connection.BeginTransaction();
        tx.Add(new Manager.ApplyGroupRulesTxFunc(first.LoadoutId, new RuleDelta { Candidates = [new GroupRule(a["A"], a["C"], GroupRuleKind.After)] }));
        tx.Add(new Manager.ApplyGroupRulesTxFunc(second.LoadoutId, new RuleDelta { Candidates = [new GroupRule(b["B"], b["C"], GroupRuleKind.After)] }));
        await tx.Commit();

        NamesByPriority(first.LoadoutId).Should().Equal("B", "C", "A");
        NamesByPriority(second.LoadoutId).Should().Equal("A", "C", "B");
    }

    [Fact]
    public async Task TwoResolversForOneLoadoutCollapseIntoTheFirst()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B", "C");
        var one = new Manager.ApplyGroupRulesTxFunc(loadout.LoadoutId, new RuleDelta { Candidates = [new GroupRule(g["A"], g["C"], GroupRuleKind.After)] });
        var two = new Manager.ApplyGroupRulesTxFunc(loadout.LoadoutId, new RuleDelta { Candidates = [new GroupRule(g["B"], g["C"], GroupRuleKind.After)] });

        one.Equals(two).Should().BeTrue();
        one.GetHashCode().Should().Be(two.GetHashCode());

        using var tx = Connection.BeginTransaction();
        tx.Add(one);
        tx.Add(two);
        await tx.Commit();

        NamesByPriority(loadout.LoadoutId).Should().Equal("B", "C", "A");
    }

    [Fact]
    public async Task ANewGroupInTheDeltaGetsItsPriorityRowAtThePositionItsRuleResolvesTo()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B");

        using var tx = Connection.BeginTransaction();
        var created = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = "New" },
        };
        var newId = LoadoutItemGroupId.From(created.Id);
        // New must end up before A, so the rule is "New before A".
        tx.Add(new Manager.ApplyGroupRulesTxFunc(loadout.LoadoutId, new RuleDelta
        {
            NewGroups = [newId],
            Candidates = [new GroupRule(newId, g["A"], GroupRuleKind.Before)],
        }));
        await tx.Commit();

        NamesByPriority(loadout.LoadoutId).Should().Equal("B", "New", "A");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL, 3UL);
    }

    [Fact]
    public async Task ARemovedGroupInTheDeltaLeavesTheRestDense()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B", "C");

        using var tx = Connection.BeginTransaction();
        tx.Delete(g["B"], recursive: true);
        tx.Delete(LoadoutItemGroupPriority.FindByTarget(Connection.Db, g["B"]).Single().Id, recursive: false);
        tx.Add(new Manager.ApplyGroupRulesTxFunc(loadout.LoadoutId, new RuleDelta { RemovedGroups = [g["B"]] }));
        await tx.Commit();

        NamesByPriority(loadout.LoadoutId).Should().Equal("A", "C");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL);
    }

    private async Task<Dictionary<string, LoadoutItemGroupId>> AddGroups(LoadoutId loadoutId, params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        var added = names.Select((name, i) =>
        {
            var group = AddEmptyGroup(tx, loadoutId, name);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)(i + 1) * 10) };
            return (name, group);
        }).ToArray();
        var result = await tx.Commit();
        return added.ToDictionary(a => a.name, a => LoadoutItemGroupId.From(result[a.group].Value));
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

    private (ulong Row, ulong Target, ulong Priority)[] PriorityRows(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Id)
        .Select(p => ((ulong)p.Id.Value, (ulong)p.TargetId.Value, (ulong)p.Priority.Value))
        .ToArray();
}
