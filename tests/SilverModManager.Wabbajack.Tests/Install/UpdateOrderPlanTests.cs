using FluentAssertions;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>The three-way rule for mod order and switches, over plain entries: no database, no job.</summary>
public class UpdateOrderPlanTests
{
    private static readonly LoadoutItemGroupId A = LoadoutItemGroupId.From(EntityId.From(1));
    private static readonly LoadoutItemGroupId B = LoadoutItemGroupId.From(EntityId.From(2));
    private static readonly LoadoutItemGroupId C = LoadoutItemGroupId.From(EntityId.From(3));
    private static readonly LoadoutItemGroupId X = LoadoutItemGroupId.From(EntityId.From(4));

    private static string NameOf(LoadoutItemGroupId id) => id == A ? "A" : id == B ? "B" : id == C ? "C" : "X";

    private static ModOrderEntry E(LoadoutItemGroupId group, bool? enabled = true) => new(group, enabled);

    private static UpdateOrderPlan Plan(ModOrderEntry[] old, ModOrderEntry[] @new, ModOrderEntry[] user)
        => UpdateOrderPlanner.Plan(old, @new, user, NameOf);

    [Fact]
    public void NothingChangedByEitherSideChangesNothing()
    {
        var order = new[] { E(A), E(B), E(C) };

        var plan = Plan(order, order, order);

        plan.NewGroups.Should().BeEmpty();
        plan.FlagWrites.Should().BeEmpty();
        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void AGroupTheListMovedAndTheUserLeftIsPlacedByTheList()
    {
        var plan = Plan([E(A), E(B), E(C)], [E(C), E(A), E(B)], [E(A), E(B), E(C)]);

        plan.NewGroups.Should().BeEquivalentTo([C]);
        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void AGroupOnlyTheUserMovedKeepsTheUsersPlaceAndIsNotAConflict()
    {
        var plan = Plan([E(A), E(B), E(C)], [E(A), E(B), E(C)], [E(C), E(A), E(B)]);

        plan.NewGroups.Should().BeEmpty();
        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void AGroupBothMovedToDifferentPlacesKeepsTheUsersPlaceAndIsAConflict()
    {
        var plan = Plan([E(A), E(B), E(C)], [E(B), E(C), E(A)], [E(B), E(A), E(C)]);

        plan.NewGroups.Should().BeEmpty();
        plan.Conflicts.Should().Contain("A");
    }

    [Fact]
    public void AGroupBothMovedToTheSamePlaceIsNotAConflict()
    {
        var plan = Plan([E(A), E(B), E(C)], [E(C), E(A), E(B)], [E(C), E(A), E(B)]);

        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void AGroupOnlyTheNewListHasIsNewWhereverTheUserPutTheOthers()
    {
        var plan = Plan([E(A), E(B)], [E(X), E(A), E(B)], [E(B), E(A)]);

        plan.NewGroups.Should().BeEquivalentTo([X]);
        plan.Entries.Select(e => e.Group).Should().Equal(X, A, B);
    }

    [Fact]
    public void ASwitchTheUserLeftTakesTheListsNewValue()
    {
        var plan = Plan([E(A, true)], [E(A, false)], [E(A, true)]);

        plan.FlagWrites.Should().Equal((A, false));
        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void ASwitchOnlyTheUserChangedStaysTheUsers()
    {
        var plan = Plan([E(A, true)], [E(A, true)], [E(A, false)]);

        plan.FlagWrites.Should().BeEmpty();
        plan.Entries.Single().Enabled.Should().BeFalse();
        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void ASwitchBothSidesFlippedToTheSameValueIsNeitherWrittenNorAConflict()
    {
        var plan = Plan([E(A, true)], [E(A, false)], [E(A, false)]);

        plan.FlagWrites.Should().BeEmpty();
        plan.Entries.Single().Enabled.Should().BeFalse();
        plan.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void AGroupTheOldProfileHadNoLineForWasOnWhenTheListSwitchesItOff()
    {
        var plan = Plan([E(A, null)], [E(A, false)], [E(A, true)]);

        plan.FlagWrites.Should().Equal((A, false));
    }

    [Fact]
    public void AGroupTheListMovedHasItsSwitchMergedForTheSinkInsteadOfWrittenTwice()
    {
        // C moves to the top, so the sink places it; the user's off switch must reach the sink as off.
        var plan = Plan([E(A), E(B), E(C)], [E(C), E(A), E(B)], [E(A), E(B), E(C, false)]);

        plan.NewGroups.Should().BeEquivalentTo([C]);
        plan.FlagWrites.Should().BeEmpty();
        plan.Entries.Single(e => e.Group == C).Enabled.Should().BeFalse();
    }
}
