using System.ComponentModel;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The File Conflicts list orders loose mods and collections together. A collection is its mods moved as one, so
/// these checks cover which priority rows a dragged row stands for and where a drop lands.
/// </summary>
public class PriorityUnitsTests
{
    // Ascending priority: Solo1, Coll(M1, M2), Solo2. Collection C is key 100.
    private static readonly LoadoutItemGroupId C = Group(100);
    private static readonly PriorityRow Solo1 = Row(1, 11), M1 = Row(2, 12, C), M2 = Row(3, 13, C), Solo2 = Row(4, 14);
    private static readonly PriorityRow[] Rows = [Solo1, M1, M2, Solo2];

    [Fact]
    public void ACollectionKeyStandsForAllItsRowsInPriorityOrder()
    {
        PriorityUnits.Expand(Rows, [C.Value]).Should().Equal(M1.Id, M2.Id);
    }

    [Fact]
    public void AModKeyStandsForItself()
    {
        PriorityUnits.Expand(Rows, [Solo1.Id.Value]).Should().Equal(Solo1.Id);
    }

    [Fact]
    public void AKeyNoRowKnowsStaysAsTheRowItNames()
    {
        PriorityUnits.Expand(Rows, [EntityId.From(999)]).Should().Equal(LoadoutItemGroupPriorityId.From(EntityId.From(999)));
    }

    [Fact]
    public void ADraggedCollectionAndOneOfItsModsCountOnce()
    {
        PriorityUnits.Expand(Rows, [C.Value, M2.Id.Value]).Should().Equal(M1.Id, M2.Id);
    }

    [Fact]
    public void DroppingAboveAModInPriorityLandsDirectlyOverIt()
    {
        PriorityUnits.TryFindLoser(Rows, [M1.Id, M2.Id], Solo2.Id.Value, aboveTarget: true, out var loser).Should().BeTrue();

        loser.Should().Be(Solo2.Id);
    }

    [Fact]
    public void DroppingBelowAModInPriorityLandsOverTheRowUnderIt()
    {
        PriorityUnits.TryFindLoser(Rows, [Solo2.Id], M1.Id.Value, aboveTarget: false, out var loser).Should().BeTrue();

        loser.Should().Be(Solo1.Id);
    }

    [Fact]
    public void DroppingBelowTheLowestRowHasNoRowToLandOver()
    {
        PriorityUnits.TryFindLoser(Rows, [Solo2.Id], Solo1.Id.Value, aboveTarget: false, out var loser).Should().BeTrue();

        loser.Should().BeNull("the rows go to the bottom of the order");
    }

    [Fact]
    public void DroppingAboveACollectionLandsOverItsTopMod()
    {
        PriorityUnits.TryFindLoser(Rows, [Solo1.Id], C.Value, aboveTarget: true, out var loser).Should().BeTrue();

        loser.Should().Be(M2.Id);
    }

    [Fact]
    public void DroppingBelowACollectionLandsOverTheRowUnderItsLowestMod()
    {
        PriorityUnits.TryFindLoser(Rows, [Solo2.Id], C.Value, aboveTarget: false, out var loser).Should().BeTrue();

        loser.Should().Be(Solo1.Id);
    }

    [Fact]
    public void AMovedRowIsNeverTheRowItLandsOver()
    {
        // Dropping the collection above itself would land it over its own top mod; the row below it is used instead.
        PriorityUnits.TryFindLoser(Rows, [M1.Id, M2.Id], C.Value, aboveTarget: true, out var loser).Should().BeTrue();

        loser.Should().Be(Solo1.Id);
    }

    [Fact]
    public void ADropOnARowNobodyKnowsIsNotGuessed()
    {
        PriorityUnits.TryFindLoser(Rows, [Solo1.Id], EntityId.From(999), aboveTarget: true, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(ListSortDirection.Ascending, true, false)]
    [InlineData(ListSortDirection.Ascending, false, true)]
    [InlineData(ListSortDirection.Descending, true, true)]
    [InlineData(ListSortDirection.Descending, false, false)]
    public void TheTopOfTheListIsLowPriorityWhenAscendingAndHighWhenDescending(ListSortDirection direction, bool visuallyAbove, bool aboveInPriority)
    {
        PriorityUnits.IsAboveInPriority(direction, visuallyAbove).Should().Be(aboveInPriority);
    }

    [Fact]
    public void ACollectionRowSitsJustBeforeItsLowestModWhenAscending()
    {
        var header = FileConflictsOrder.Anchor(isCollection: true, index: 0, lowest: 2, highest: 3, ListSortDirection.Ascending);
        var mod = FileConflictsOrder.Anchor(isCollection: false, index: 2, lowest: 0, highest: 0, ListSortDirection.Ascending);

        FileConflictsOrder.Compare(header, mod, ListSortDirection.Ascending).Should().BeNegative();
        FileConflictsOrder.Compare(mod, header, ListSortDirection.Ascending).Should().BePositive();
    }

    [Fact]
    public void ACollectionRowSitsJustBeforeItsHighestModWhenDescending()
    {
        var header = FileConflictsOrder.Anchor(isCollection: true, index: 0, lowest: 2, highest: 3, ListSortDirection.Descending);
        var top = FileConflictsOrder.Anchor(isCollection: false, index: 3, lowest: 0, highest: 0, ListSortDirection.Descending);
        var above = FileConflictsOrder.Anchor(isCollection: false, index: 4, lowest: 0, highest: 0, ListSortDirection.Descending);

        FileConflictsOrder.Compare(above, header, ListSortDirection.Descending).Should().BeNegative();
        FileConflictsOrder.Compare(header, top, ListSortDirection.Descending).Should().BeNegative();
    }

    private static LoadoutItemGroupId Group(ulong n) => LoadoutItemGroupId.From(EntityId.From(n));

    private static PriorityRow Row(ulong id, ulong group, LoadoutItemGroupId? collection = null)
        => new(LoadoutItemGroupPriorityId.From(EntityId.From(id)), Group(group), collection);
}
