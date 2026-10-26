using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Sorting;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Priority order is ascending and the highest priority wins, so "A After B" puts A later in the order than B.
/// </summary>
public class RuleResolverTests
{
    private static readonly LoadoutItemGroupId A = Id(1), B = Id(2), C = Id(3), D = Id(4), E = Id(5);

    [Fact]
    public void AChainOfAfterRulesReordersTheWholeChain()
    {
        // A after B after C: C first, then B, then A.
        var result = RuleResolver.Resolve([A, B, C], [After(A, B), After(B, C)]);

        result.Cycle.Should().BeNull();
        result.Order.Should().Equal(C, B, A);
    }

    [Fact]
    public void BeforeIsTheMirrorOfAfter()
    {
        var result = RuleResolver.Resolve([A, B, C], [Rule(C, A, GroupRuleKind.Before)]);

        result.Order.Should().Equal(B, C, A);
    }

    [Fact]
    public void AnUnconstrainedGroupKeepsItsPlace()
    {
        var result = RuleResolver.Resolve([A, B, C, D], [After(A, B)]);

        result.Order.Should().Equal(B, A, C, D);
    }

    [Fact]
    public void AnAlreadySatisfiedRuleChangesNothing()
    {
        // D sits after C already; E has no rules and sits between them in the old order.
        var result = RuleResolver.Resolve([C, E, D], [After(D, C)]);

        result.Order.Should().Equal(C, E, D);
    }

    [Fact]
    public void ACycleNamesTheTwoGroupsAndNeverThrows()
    {
        var result = RuleResolver.Resolve([A, B, C], [After(A, B), After(B, A)]);

        result.Cycle.Should().NotBeNull();
        new[] { result.Cycle!.Value.First, result.Cycle.Value.Second }.Should().BeEquivalentTo([A, B]);
    }

    [Fact]
    public void ALongerCycleNamesTwoGroupsOnIt()
    {
        var result = RuleResolver.Resolve([A, B, C, D], [After(A, B), After(B, C), After(C, A)]);

        result.Cycle.Should().NotBeNull();
        new[] { result.Cycle!.Value.First, result.Cycle.Value.Second }.Should().BeSubsetOf([A, B, C]);
    }

    [Fact]
    public void ACandidateRuleThatIsNotStoredIsHonoured()
    {
        var delta = new RuleDelta { Candidates = [After(A, C)] };

        var result = RuleResolver.Resolve([A, B, C], [], delta);

        result.Order.Should().Equal(B, C, A);
    }

    [Fact]
    public void ACandidateThatClosesACycleWithStoredRulesNamesTheCandidateGroups()
    {
        var delta = new RuleDelta { Candidates = [After(B, A)] };

        var result = RuleResolver.Resolve([A, B, C], [After(A, B)], delta);

        result.Cycle.Should().NotBeNull();
        new[] { result.Cycle!.Value.First, result.Cycle.Value.Second }.Should().BeEquivalentTo([A, B]);
    }

    [Fact]
    public void ANewGroupLandsWhereItsRulesSayAndOtherwiseAtTheEnd()
    {
        var withRule = RuleResolver.Resolve([A, B], [Rule(D, A, GroupRuleKind.Before)], new RuleDelta { NewGroups = [D] });
        withRule.Order.Should().Equal(B, D, A);

        var withoutRule = RuleResolver.Resolve([A, B], [], new RuleDelta { NewGroups = [C, D] });
        withoutRule.Order.Should().Equal(A, B, C, D);
    }

    [Fact]
    public void ANewGroupWithAnAfterRuleLandsAfterItsTarget()
    {
        var result = RuleResolver.Resolve([A, B, C], [After(D, A)], new RuleDelta { NewGroups = [D] });

        result.Order.Should().Equal(A, B, C, D);
    }

    [Fact]
    public void ARemovedGroupIsDroppedAndRulesNamingItAreIgnored()
    {
        var delta = new RuleDelta { RemovedGroups = [B] };

        var result = RuleResolver.Resolve([A, B, C], [After(A, B), After(C, B)], delta);

        result.Cycle.Should().BeNull();
        result.Order.Should().Equal(A, C);
    }

    private static LoadoutItemGroupId Id(ulong n) => LoadoutItemGroupId.From(EntityId.From(n));
    private static GroupRule After(LoadoutItemGroupId source, LoadoutItemGroupId other) => Rule(source, other, GroupRuleKind.After);
    private static GroupRule Rule(LoadoutItemGroupId source, LoadoutItemGroupId other, GroupRuleKind kind) => new(source, other, kind);
}
