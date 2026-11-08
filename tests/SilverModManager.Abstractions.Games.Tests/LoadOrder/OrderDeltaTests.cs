using FluentAssertions;
using SilverModManager.Abstractions.Games.LoadOrder;

namespace SilverModManager.Abstractions.Games.Tests.LoadOrder;

public class OrderDeltaTests
{
    [Fact]
    public void OneInsertedEntryMovesNothingElse()
    {
        // X is new, not moved: callers treat added entries separately, and A/B/C keep their relative order.
        OrderDelta.Moved(["A", "B", "C"], ["X", "A", "B", "C"]).Should().BeEmpty();
    }

    [Fact]
    public void ASwappedPairReportsOnlyOneMovedEntry()
    {
        var moved = OrderDelta.Moved(["A", "B", "C"], ["B", "A", "C"]);

        moved.Should().HaveCount(1);
        moved.Should().BeSubsetOf(new[] { "A", "B" });
    }

    [Fact]
    public void EntriesOnlyInOneSequenceAreIgnoredForMovement()
    {
        // D left the list and X joined it; neither is in both, so only the reorder of A/B/C counts.
        OrderDelta.Moved(["A", "D", "B", "C"], ["A", "B", "X", "C"]).Should().BeEmpty();
    }

    [Fact]
    public void AnUnchangedOrderMovesNothing()
    {
        OrderDelta.Moved(["A", "B", "C"], ["A", "B", "C"]).Should().BeEmpty();
    }

    [Fact]
    public void AnEntryMovedAcrossTheListIsTheOnlyOneReported()
    {
        OrderDelta.Moved(["A", "B", "C", "D", "E"], ["B", "C", "D", "E", "A"]).Should().BeEquivalentTo(new[] { "A" });
    }

    [Fact]
    public void TheComparerDecidesWhichEntriesAreTheSame()
    {
        var moved = OrderDelta.Moved(["a", "b", "c"], ["B", "C", "A"], StringComparer.OrdinalIgnoreCase);

        moved.Should().HaveCount(1);
        moved.Should().Contain("A");
    }

    [Fact]
    public void ThreeWayFollowsTheListWhenTheUserDidNotTouchTheValue()
    {
        OrderDelta.ThreeWay(old: true, @new: false, user: true).Should().Be((false, false));
    }

    [Fact]
    public void ThreeWayKeepsTheUsersValueAndFlagsTheConflictWhenBothChanged()
    {
        OrderDelta.ThreeWay(old: "a", @new: "b", user: "c").Should().Be(("c", true));
    }

    [Fact]
    public void ThreeWayKeepsTheUsersValueWithoutConflictWhenOnlyTheUserChanged()
    {
        OrderDelta.ThreeWay(old: "a", @new: "a", user: "c").Should().Be(("c", false));
    }

    [Fact]
    public void ThreeWayKeepsTheValueWhenNobodyChangedIt()
    {
        OrderDelta.ThreeWay(old: "a", @new: "a", user: "a").Should().Be(("a", false));
    }

    [Fact]
    public void ThreeWayUsesTheComparerForEquality()
    {
        OrderDelta.ThreeWay("a", "B", "A", StringComparer.OrdinalIgnoreCase).Should().Be(("B", false));
    }

    [Fact]
    public void ThreeWayRaisesNoConflictWhenTheUserAndTheListAgree()
    {
        OrderDelta.ThreeWay(old: "a", @new: "b", user: "b").Should().Be(("b", false));
    }

    [Fact]
    public void AReversedLongListReportsEveryEntryButOneAsMoved()
    {
        var previous = Enumerable.Range(0, 3000).Select(i => $"m{i}").ToArray();
        var current = previous.Reverse().ToArray();

        OrderDelta.Moved(previous, current).Should().HaveCount(2999);
    }

    [Fact]
    public void ADuplicateKeyInThePreviousSequenceIsRejected()
    {
        var act = () => OrderDelta.Moved(["A", "B", "A"], ["A", "B"]);

        act.Should().Throw<ArgumentException>().WithMessage("*duplicate key*");
    }

    [Fact]
    public void ADuplicateKeyInTheCurrentSequenceIsRejected()
    {
        var act = () => OrderDelta.Moved(["A", "B"], ["A", "B", "B"]);

        act.Should().Throw<ArgumentException>().WithMessage("*duplicate key*");
    }
}
