using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// A priority move that would break a group rule is refused and writes nothing; one that keeps every rule goes through.
/// </summary>
public class ConflictMoveRuleTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ConflictMoveRuleTests>(helper)
{
    [Fact]
    public async Task ResolvingInFavourOfAGroupThatTheRuleKeepsBelowIsRefused()
    {
        var (loadout, groups, rule) = await Seed(GroupRuleKind.Before, source: "A", other: "C");
        var before = PriorityRows(loadout.LoadoutId);

        // A has to stay below C, and this puts A directly above C.
        var result = await LoadoutManager.ResolveFileConflicts([groups["A"].priority], groups["C"].priority);

        result.IsRuleViolation.Should().BeTrue();
        result.Rule.LoadoutGroupRuleId.Should().Be(rule);
        PriorityRows(loadout.LoadoutId).Should().Equal(before);
    }

    [Fact]
    public async Task LosingAllConflictsIsRefusedWhenTheGroupHasToStayAbove()
    {
        var (loadout, groups, rule) = await Seed(GroupRuleKind.After, source: "C", other: "A");
        var before = PriorityRows(loadout.LoadoutId);

        // C has to win over A, and this sends C to the bottom.
        var result = await LoadoutManager.LoseAllFileConflicts([groups["C"].priority]);

        result.IsRuleViolation.Should().BeTrue();
        result.Rule.LoadoutGroupRuleId.Should().Be(rule);
        PriorityRows(loadout.LoadoutId).Should().Equal(before);
    }

    [Fact]
    public async Task WinningAllConflictsIsRefusedWhenTheGroupHasToStayBelow()
    {
        var (loadout, groups, rule) = await Seed(GroupRuleKind.Before, source: "A", other: "D");
        var before = PriorityRows(loadout.LoadoutId);

        // A has to lose to D, and this puts A on top.
        var result = await LoadoutManager.WinAllFileConflicts([groups["A"].priority]);

        result.IsRuleViolation.Should().BeTrue();
        result.Rule.LoadoutGroupRuleId.Should().Be(rule);
        PriorityRows(loadout.LoadoutId).Should().Equal(before);
    }

    [Fact]
    public async Task AMoveThatKeepsTheRuleIsDone()
    {
        var (loadout, groups, _) = await Seed(GroupRuleKind.After, source: "C", other: "A");

        // D moves above B; C stays above A.
        var result = await LoadoutManager.ResolveFileConflicts([groups["D"].priority], groups["B"].priority);

        result.IsRuleViolation.Should().BeFalse();
        NamesByPriority(loadout.LoadoutId).Should().Equal("A", "B", "D", "C");
    }

    [Fact]
    public async Task ARuleTheOrderAlreadyBreaksDoesNotStopAnUnrelatedMove()
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C", "D");
        // Stored out of order on purpose: A should win over D but sits below it.
        await AddRule(loadout.LoadoutId, groups["A"].group, groups["D"].group, GroupRuleKind.After);

        var result = await LoadoutManager.ResolveFileConflicts([groups["D"].priority], groups["B"].priority);

        result.IsRuleViolation.Should().BeFalse();
        NamesByPriority(loadout.LoadoutId).Should().Equal("A", "B", "D", "C");
    }

    private async Task<(Loadout.ReadOnly Loadout, Dictionary<string, (LoadoutItemGroupId group, LoadoutItemGroupPriorityId priority)> Groups, LoadoutGroupRuleId Rule)> Seed(
        GroupRuleKind kind, string source, string other)
    {
        var loadout = await CreateLoadout();
        var groups = await AddGroupsWithPriorities(loadout.LoadoutId, "A", "B", "C", "D");
        var rule = await AddRule(loadout.LoadoutId, groups[source].group, groups[other].group, kind);
        return (loadout, groups, rule);
    }

    private async Task<LoadoutGroupRuleId> AddRule(LoadoutId loadoutId, LoadoutItemGroupId source, LoadoutItemGroupId other, GroupRuleKind kind)
    {
        using var tx = Connection.BeginTransaction();
        var rule = new LoadoutGroupRule.New(tx) { LoadoutId = loadoutId, SourceId = source, OtherId = other, Kind = kind };
        var result = await tx.Commit();
        return LoadoutGroupRuleId.From(result[rule.Id].Value);
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
            a => (LoadoutItemGroupId.From(result[a.group].Value), LoadoutItemGroupPriorityId.From(result[a.priority.Id].Value)));
    }

    private string[] NamesByPriority(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => p.Target.AsLoadoutItem().Name)
        .ToArray();

    private (ulong Row, ulong Target, ulong Priority)[] PriorityRows(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Id)
        .Select(p => ((ulong)p.Id.Value, (ulong)p.TargetId.Value, (ulong)p.Priority.Value))
        .ToArray();
}
