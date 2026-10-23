using FluentAssertions;
using NexusMods.Games.Bethesda.LoadOrder.Formats;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.SortOrder;

public class PluginListDifferenceTests
{
    private static PluginListEntry On(string name) => new(name, true);
    private static PluginListEntry Off(string name) => new(name, false);

    [Fact]
    public void ALineForLineCopy_IsEmpty()
    {
        PluginListDifference.Between([On("A.esp"), Off("B.esp")], [On("A.esp"), Off("B.esp")]).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AnUnstarredPlugin_IsSwitchedOff_AndAStarredOne_IsSwitchedOn()
    {
        var diff = PluginListDifference.Between([On("A.esp"), Off("B.esp")], [Off("A.esp"), On("B.esp")]);

        diff.SwitchedOff.Should().Equal("A.esp");
        diff.SwitchedOn.Should().Equal("B.esp");
        diff.Moved.Should().BeEmpty();
    }

    [Fact]
    public void ALineTheFileLeavesOut_CountsAsSwitchedOff_ButNotForAnInactivePlugin()
    {
        var diff = PluginListDifference.Between([On("A.esp"), Off("B.esp")], []);

        diff.SwitchedOff.Should().Equal("A.esp");
        diff.SwitchedOn.Should().BeEmpty();
    }

    [Fact]
    public void WhenTheFileCannotCarrySwitches_OnlyTheOrderIsCompared()
    {
        var expected = new[] { On("A.esp"), Off("B.esp"), On("C.esp") };
        var onDisk = new[] { On("A.esp"), On("B.esp"), On("C.esp") };

        PluginListDifference.Between(expected, onDisk, comparesSwitches: false).IsEmpty.Should().BeTrue();
        PluginListDifference.Between(expected, [On("C.esp"), On("A.esp"), On("B.esp")], comparesSwitches: false).Moved.Should().Equal("C.esp");
    }

    [Fact]
    public void Combining_UnitesEachKindAndKeepsTheNamesDistinct()
    {
        var first = new PluginListDifference(["A.esp"], [], ["C.esp"]);
        var second = new PluginListDifference([], ["B.esp"], ["c.esp", "D.esp"]);

        var both = PluginListDifference.Combine(first, second);

        both.SwitchedOff.Should().Equal("A.esp");
        both.SwitchedOn.Should().Equal("B.esp");
        both.Moved.Should().Equal("C.esp", "D.esp");
    }

    [Fact]
    public void NamesAreComparedIgnoringCase()
    {
        PluginListDifference.Between([On("Mine.esp")], [On("mine.ESP")]).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void UnknownNamesAndEarlyLoaders_AreIgnored()
    {
        var expected = new[] { new PluginListEntry("Skyrim.esm", true, true), On("A.esp") };

        PluginListDifference.Between(expected, [Off("Skyrim.esm"), On("Ghost.esp"), On("A.esp")]).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ASwapOfTwoNeighbours_IsOneMove()
    {
        var diff = PluginListDifference.Between([On("A.esp"), On("B.esp"), On("C.esp")], [On("B.esp"), On("A.esp"), On("C.esp")]);

        diff.Moved.Should().HaveCount(1);
    }

    [Fact]
    public void APluginDraggedAcrossManyOthers_IsOneMove()
    {
        var expected = new[] { "A", "B", "C", "D", "E" }.Select(n => On(n + ".esp")).ToList();
        var onDisk = new[] { "E", "A", "B", "C", "D" }.Select(n => On(n + ".esp")).ToList();

        PluginListDifference.Between(expected, onDisk).Moved.Should().Equal("E.esp");
    }

    [Fact]
    public void ARemovedLine_DoesNotMoveTheOthers()
    {
        var diff = PluginListDifference.Between([On("A.esp"), On("B.esp"), On("C.esp")], [On("A.esp"), On("C.esp")]);

        diff.Moved.Should().BeEmpty();
        diff.SwitchedOff.Should().Equal("B.esp");
    }

    [Fact]
    public void TheSummary_GivesTheCountsThenTheNames()
    {
        var diff = new PluginListDifference(["A.esp"], ["B.esp", "C.esp"], ["D.esp"]);

        diff.Summary().Should().Be("1 switched off, 2 switched on, 1 moved: A.esp, B.esp, C.esp, D.esp");
    }

    [Fact]
    public void TheSummary_ListsTenNamesAndCountsTheRest()
    {
        var names = Enumerable.Range(1, 13).Select(i => $"P{i}.esp").ToList();

        new PluginListDifference(names, [], []).Summary()
            .Should().Be("13 switched off: " + string.Join(", ", names.Take(10)) + " and 3 more");
    }
}
