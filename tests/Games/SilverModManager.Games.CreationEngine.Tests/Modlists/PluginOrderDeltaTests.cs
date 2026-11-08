using FluentAssertions;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.CreationEngine.SortOrder;

namespace SilverModManager.Games.CreationEngine.Tests.Modlists;

public class PluginOrderDeltaTests
{
    private static readonly ISet<string> Early = new HashSet<string>(["Skyrim.esm"], PluginNameComparer.Instance);

    private static List<(string Name, bool? IsEnabled)> Listed(params string[] names) => names.Select(n => (n, (bool?)true)).ToList();

    private static PluginSortData[] Rows(params string[] names) =>
        names.Select((name, i) => new PluginSortData(name, i, true, earlyLoaderIndex: name == "Skyrim.esm" ? 0 : null)).ToArray();

    private static string[] Names(IReadOnlyList<(string Name, bool? IsEnabled)> order) => order.Select(o => o.Name).ToArray();

    [Fact]
    public void APluginNewInTheCurrentProfileIsInsertedAfterItsProfilePredecessor()
    {
        var conflicts = new List<string>();

        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "A.esp", "B.esp", "Mine.esp"),
            Listed("A.esp", "B.esp"), Listed("A.esp", "New.esp", "B.esp"), Early, conflicts);

        Names(order).Should().Equal("Skyrim.esm", "A.esp", "New.esp", "B.esp", "Mine.esp");
        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void InsertingOnePluginAtTheTopMovesNothingElse()
    {
        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "Mine.esp", "A.esp", "B.esp"),
            Listed("A.esp", "B.esp"), Listed("Top.esp", "A.esp", "B.esp"), Early, []);

        Names(order).Should().Equal("Skyrim.esm", "Top.esp", "Mine.esp", "A.esp", "B.esp");
    }

    [Fact]
    public void TwoPluginsTheUserSwappedKeepTheUsersOrder()
    {
        // The user put B ahead of A; the list moved nothing, so the order stays and nothing is reported.
        var conflicts = new List<string>();

        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "B.esp", "A.esp", "C.esp"),
            Listed("A.esp", "B.esp", "C.esp"), Listed("A.esp", "B.esp", "C.esp"), Early, conflicts);

        Names(order).Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void APluginTheListMovedAndTheUserLeftIsPlacedAfterItsNewPredecessor()
    {
        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "A.esp", "B.esp", "C.esp", "D.esp"),
            Listed("A.esp", "B.esp", "C.esp", "D.esp"), Listed("A.esp", "C.esp", "D.esp", "B.esp"), Early, []);

        Names(order).Should().Equal("Skyrim.esm", "A.esp", "C.esp", "D.esp", "B.esp");
    }

    [Fact]
    public void APluginBothTheUserAndTheListMovedKeepsTheUsersPlaceAndIsListedAsAConflict()
    {
        var conflicts = new List<string>();

        // The user moved C between A and B; the list moved it to the front.
        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "A.esp", "C.esp", "B.esp"),
            Listed("A.esp", "B.esp", "C.esp"), Listed("C.esp", "A.esp", "B.esp"), Early, conflicts);

        Names(order).Should().Equal("Skyrim.esm", "A.esp", "C.esp", "B.esp");
        conflicts.Should().Equal("C.esp");
    }

    [Fact]
    public void ABlockTheUserMovedThatTheListReorderedStillGetsTheListsMoveOrAConflict()
    {
        // The user moved D and E together ahead of B and C; the list swapped D and E. E's predecessor in the
        // user's order is D, as it was before, so the user did not move E relative to it and the list's move applies.
        var conflicts = new List<string>();

        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "A.esp", "D.esp", "E.esp", "B.esp", "C.esp"),
            Listed("A.esp", "B.esp", "C.esp", "D.esp", "E.esp"), Listed("A.esp", "B.esp", "C.esp", "E.esp", "D.esp"), Early, conflicts);

        Names(order).Should().Equal("Skyrim.esm", "A.esp", "D.esp", "B.esp", "C.esp", "E.esp");
        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void APluginTheUserAddedBesideAMovedPluginDoesNotMakeItAConflict()
    {
        // Both sides put C first; the user's own plugin sitting before it is not a move of C.
        var conflicts = new List<string>();

        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "Mine.esp", "C.esp", "A.esp", "B.esp"),
            Listed("A.esp", "B.esp", "C.esp"), Listed("C.esp", "A.esp", "B.esp"), Early, conflicts);

        Names(order).Should().Equal("Skyrim.esm", "Mine.esp", "C.esp", "A.esp", "B.esp");
        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void APluginWhoseSwitchChangedBetweenProfilesTakesTheNewSwitchWhenTheUserLeftIt()
    {
        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "A.esp", "B.esp"),
            [("A.esp", true), ("B.esp", true)], [("A.esp", false), ("B.esp", true)], Early, []);

        order.Should().Equal(("Skyrim.esm", (bool?)null), ("A.esp", false), ("B.esp", null));
    }

    [Fact]
    public void APluginTheUserSwitchedAndTheListSwitchedTheSameWayKeepsTheUsersSwitchWithoutAConflict()
    {
        // Two switches cannot disagree on a change from the same base, so a switch never raises a conflict.
        var conflicts = new List<string>();
        var rows = new[] { new PluginSortData("A.esp", 0, false) };

        var order = PluginOrderDelta.Build(rows, [("A.esp", true)], [("A.esp", false)], Early, conflicts);

        order.Should().Equal(("A.esp", (bool?)null));
        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void APluginTheUserSwitchedWhileTheListLeftItKeepsTheUsersSwitch()
    {
        var rows = new[] { new PluginSortData("A.esp", 0, false) };

        var order = PluginOrderDelta.Build(rows, [("A.esp", true)], [("A.esp", true)], Early, []);

        order.Should().Equal(("A.esp", (bool?)null));
    }

    [Fact]
    public void RowsTheProfilesDoNotMentionKeepTheirPlaceAndNullSwitch()
    {
        var order = PluginOrderDelta.Build(
            Rows("Skyrim.esm", "Mine1.esp", "A.esp", "Mine2.esp"),
            Listed("A.esp"), Listed("A.esp", "New.esp"), Early, []);

        order.Should().Equal(
            ("Skyrim.esm", (bool?)null), ("Mine1.esp", null), ("A.esp", null), ("New.esp", true), ("Mine2.esp", null));
    }
}
