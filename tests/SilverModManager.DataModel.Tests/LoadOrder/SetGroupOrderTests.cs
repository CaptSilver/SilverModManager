using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.LoadOrder;

/// <summary>
/// Setting a group order re-deals the slots the listed groups hold, then lets the stored rules have the last word.
/// </summary>
public class SetGroupOrderTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<SetGroupOrderTests>(helper)
{
    /// <summary>Groups by name; the ones named in <paramref name="withRows"/> get rows 1..N in that order.</summary>
    private async Task<(Loadout.ReadOnly Loadout, Dictionary<string, LoadoutItemGroupId> Groups)> Seed(string[] withRows, params string[] withoutRows)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var added = withRows.Concat(withoutRows).ToDictionary(name => name, name => AddEmptyGroup(tx, loadout.LoadoutId, name));
        for (var i = 0; i < withRows.Length; i++)
        {
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = added[withRows[i]], Priority = ConflictPriority.From((ulong)i + 1) };
        }
        var result = await tx.Commit();
        return (loadout, added.ToDictionary(pair => pair.Key, pair => LoadoutItemGroupId.From(result[pair.Value].Value)));
    }

    private async Task<GroupOrderResult> Set(LoadoutId loadoutId, params LoadoutItemGroupId[] winnerToLoser)
    {
        using var tx = Connection.BeginTransaction();
        var result = LoadoutManager.SetGroupOrder(loadoutId, winnerToLoser, tx);
        await tx.Commit();
        return result;
    }

    private string[] NamesByPriority(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => p.Target.AsLoadoutItem().Name)
        .ToArray();

    [Fact]
    public async Task RowsTheOrderCreatesDoNotMergeWithEntitiesTheSameTransactionAdds()
    {
        var (loadout, g) = await Seed(["a"], "n", "m");

        // The transaction creates its own entities as well as the rows SetGroupOrder makes for n and m.
        using var tx = Connection.BeginTransaction();
        var extra = AddEmptyGroup(tx, loadout.LoadoutId, "extra");
        LoadoutManager.SetGroupOrder(loadout.LoadoutId, [g["n"], g["m"], g["a"]], tx);
        var result = await tx.Commit();

        var extraId = LoadoutItemGroupId.From(result[extra].Value);
        LoadoutItem.Load(Connection.Db, extraId).Name.Should().Be("extra");
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).Select(row => row.Id)
            .Should().NotContain(extraId.Value, "a priority row is its own entity, not the group the transaction added");
        NamesByPriority(loadout.LoadoutId).Should().Equal("a", "m", "n");
    }

    [Fact]
    public async Task GroupsWithRowsAreReorderedWithinTheirOwnSlots()
    {
        var (loadout, g) = await Seed(["a", "b", "c", "d"]);

        // d wins over everything and stays there; a and c swap the slots they hold.
        await Set(loadout.LoadoutId, g["a"], g["c"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("c", "b", "a", "d");
    }

    [Fact]
    public async Task AGroupWithoutARowGoesAboveEveryRow()
    {
        var (loadout, g) = await Seed(["a", "b"], "n", "m");

        // No listed group has a row, so the whole block goes on top, in the listed order.
        await Set(loadout.LoadoutId, g["n"], g["m"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("a", "b", "m", "n");
    }

    [Fact]
    public async Task ARowlessGroupInsideTheListTakesItsListedPlace()
    {
        var (loadout, g) = await Seed(["a", "b", "o"], "n");

        await Set(loadout.LoadoutId, g["b"], g["n"], g["a"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("a", "n", "b", "o");
    }

    [Fact]
    public async Task ARowlessGroupBelowTheListedGroupsGoesBelowTheirSlots()
    {
        var (loadout, g) = await Seed(["a", "o", "b"], "n");

        // n is the lowest of the block, so it sits right below the first slot the block holds.
        await Set(loadout.LoadoutId, g["b"], g["a"], g["n"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("n", "a", "o", "b");
    }

    [Fact]
    public async Task AStoredRuleBeatsTheRequestedOrderAndIsReported()
    {
        var (loadout, g) = await Seed(["a", "b"]);
        var added = await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["b"], GroupRuleKind.After);
        var ruleId = added.RuleId;

        var result = await Set(loadout.LoadoutId, g["b"], g["a"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("b", "a");
        result.Resolved.Should().Equal(g["a"], g["b"]);
        result.Overridden.Should().ContainSingle()
            .Which.Should().Match<OverriddenOrder>(o => o.RequestedWinner == g["b"] && o.RequestedLoser == g["a"] && o.Rule.LoadoutGroupRuleId == ruleId);
    }

    [Fact]
    public async Task APairTurnedRoundOnlyThroughRulesOnUnlistedGroupsIsReported()
    {
        var (loadout, g) = await Seed(["b", "o", "a"]);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["o"], GroupRuleKind.After);
        var oAfterB = (await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["o"], g["b"], GroupRuleKind.After)).RuleId;

        // b above a would put o between them the wrong way for both rules; neither rule names the pair.
        var result = await Set(loadout.LoadoutId, g["b"], g["a"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("b", "o", "a");
        var overridden = result.Overridden.Should().ContainSingle().Subject;
        overridden.RequestedWinner.Should().Be(g["b"]);
        overridden.RequestedLoser.Should().Be(g["a"]);
        // The chain is b -> o -> a; the first link the projection breaks is blamed.
        overridden.Rule.LoadoutGroupRuleId.Should().Be(oAfterB);
    }

    [Fact]
    public async Task AGroupDisplacedByARuleIsReportedAsDisplaced()
    {
        var (loadout, g) = await Seed(["x", "z", "y"]);
        var zAfterX = (await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["z"], g["x"], GroupRuleKind.After)).RuleId;

        // z wins over x by rule, so x above z is forced round; y only ends below z because the sort made room.
        var result = await Set(loadout.LoadoutId, g["x"], g["y"], g["z"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("y", "x", "z");
        result.Resolved.Should().Equal(g["z"], g["x"], g["y"]);
        result.Overridden.Select(o => (o.RequestedWinner, o.RequestedLoser, o.Displaced, o.Rule.LoadoutGroupRuleId))
            .Should().Equal((g["x"], g["z"], false, zAfterX), (g["y"], g["z"], true, zAfterX));
    }

    [Fact]
    public async Task AGroupMovedToRepairARuleOnAnUnlistedGroupIsReportedAsDisplaced()
    {
        var (loadout, g) = await Seed(["w", "y", "u", "z"]);
        var zAfterU = (await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["z"], g["u"], GroupRuleKind.After)).RuleId;

        var result = await Set(loadout.LoadoutId, g["w"], g["y"], g["z"]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("y", "u", "z", "w");
        result.Overridden.Should().Contain(o => o.RequestedWinner == g["y"] && o.RequestedLoser == g["z"] && o.Displaced && o.Rule.LoadoutGroupRuleId == zAfterU);
    }

    [Fact]
    public async Task AGroupListedTwiceIsRefused()
    {
        var (loadout, g) = await Seed(["a", "b"]);
        using var tx = Connection.BeginTransaction();

        var act = () => LoadoutManager.SetGroupOrder(loadout.LoadoutId, [g["a"], g["b"], g["a"]], tx);

        act.Should().Throw<ArgumentException>().WithMessage("*listed twice*");
    }

    [Fact]
    public async Task ARuleTheRequestedOrderKeepsIsNotReported()
    {
        var (loadout, g) = await Seed(["a", "b"]);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["b"], GroupRuleKind.After);

        var result = await Set(loadout.LoadoutId, g["a"], g["b"]);

        result.Overridden.Should().BeEmpty();
    }

    [Fact]
    public async Task NoRuleRowsAreCreated()
    {
        var (loadout, g) = await Seed(["a", "b", "c"]);
        var before = LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Count;

        await Set(loadout.LoadoutId, g["a"], g["c"], g["b"]);

        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Count.Should().Be(before);
    }

    [Fact]
    public async Task AnEmptyListAddsNoTxFunc()
    {
        var (loadout, g) = await Seed(["a"]);
        using var tx = Connection.BeginTransaction();

        var result = LoadoutManager.SetGroupOrder(loadout.LoadoutId, [], tx);

        result.Overridden.Should().BeEmpty();
        // A second kind of priority writer for the loadout throws if SetGroupOrder had added its func.
        tx.Add(new SilverModManager.DataModel.LoadoutManager.AddPriorityTxFunc(loadout.LoadoutId, g["a"]));
        var act = async () => await tx.Commit();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GroupRuleTextNamesWinnerThenLoser()
    {
        var (loadout, g) = await Seed(["a", "b"]);
        var after = (await LoadoutManager.AddGroupRule(loadout.LoadoutId, g["a"], g["b"], GroupRuleKind.After)).RuleId;
        GroupRuleText.WinsOver(LoadoutGroupRule.Load(Connection.Db, after)).Should().Be("a wins over b");

        var (other, h) = await Seed(["a", "b"]);
        var before = (await LoadoutManager.AddGroupRule(other.LoadoutId, h["a"], h["b"], GroupRuleKind.Before)).RuleId;
        GroupRuleText.WinsOver(LoadoutGroupRule.Load(Connection.Db, before)).Should().Be("b wins over a");
    }
}
