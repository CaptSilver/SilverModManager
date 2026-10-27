using FluentAssertions;
using SilverModManager.Abstractions.Games.LoadOrder;

namespace SilverModManager.DataModel.Tests.LoadOrder;

/// <summary>A later run of a source keeps the order the user has and slots in only what the run added.</summary>
public class ListedOrderTests
{
    private static List<string> Insert(string[] listed, string[] current, params string[] added) =>
        ListedOrder.InsertNew(listed, current, added.ToHashSet(StringComparer.OrdinalIgnoreCase).Contains, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheCurrentOrderIsKeptAndANewItemGoesRightAfterItsListedPredecessor()
    {
        // The source lists a, n, b, c; the user has since put c first.
        Insert(["a", "n", "b", "c"], ["c", "a", "b"], "n").Should().Equal("c", "a", "n", "b");
    }

    [Fact]
    public void ANewItemListedFirstGoesRightBeforeItsListedSuccessor()
    {
        Insert(["n", "a", "b"], ["b", "a"], "n").Should().Equal("b", "n", "a");
    }

    [Fact]
    public void NewItemsListedTogetherStayTogetherInTheirListedOrder()
    {
        Insert(["a", "n1", "n2", "b"], ["b", "a"], "n1", "n2").Should().Equal("b", "a", "n1", "n2");
    }

    [Fact]
    public void ANewItemAlreadyInTheCurrentOrderIsMovedToItsListedSlot()
    {
        Insert(["a", "n", "b"], ["n", "a", "b"], "n").Should().Equal("a", "n", "b");
    }

    [Fact]
    public void ANewItemWithNoListedNeighbourInTheOrderGoesLast()
    {
        Insert(["ghost", "n"], ["x"], "n").Should().Equal("x", "n");
    }

    [Fact]
    public void AnUnlistedItemOfTheCurrentOrderKeepsItsPlace()
    {
        Insert(["a", "n"], ["a", "mine", "b"], "n").Should().Equal("a", "n", "mine", "b");
    }

    [Fact]
    public void ItemsAreComparedWithTheGivenComparer()
    {
        Insert(["A.esp", "N.esp", "B.esp"], ["b.esp", "a.esp"], "n.esp").Should().Equal("b.esp", "a.esp", "N.esp");
    }
}
