using Avalonia;
using FluentAssertions;
using SilverModManager.App.UI.Pages.MyGames;

namespace SilverModManager.UI.Tests.Pages;

public class GameTileDragLayoutTests
{
    // Seven 100 px tiles, three to a row, 10 px apart. Row centres sit at y = 50, 160 and 270; column centres at x = 50, 160, 270.
    //   0 1 2
    //   3 4 5
    //   6
    private static readonly Rect[] Slots = Enumerable.Range(0, 7)
        .Select(i => new Rect(i % 3 * 110, i / 3 * 110, 100, 100))
        .ToArray();

    private static Rect Dragged(int from, double dx, double dy) => Slots[from].Translate(new Vector(dx, dy));

    [Theory]
    [InlineData(1, 0, 0, 1)]
    // The leading (right) edge reaches the next tile's centre (x = 270) after 60 px of travel.
    [InlineData(1, 59, 0, 1)]
    [InlineData(1, 61, 0, 2)]
    // Left edge reaching the previous tile's centre (x = 50) takes 60 px the other way.
    [InlineData(1, -59, 0, 1)]
    [InlineData(1, -61, 0, 0)]
    // Moving along a row never reaches into the next one.
    [InlineData(2, 500, 0, 2)]
    [InlineData(0, -500, 0, 0)]
    public void TargetIndex_gives_way_when_the_leading_edge_passes_a_neighbours_centre_along_a_row(int from, double dx, double dy, int expected)
    {
        GameTileDragLayout.TargetIndex(Slots, from, Dragged(from, dx, dy)).Should().Be(expected);
    }

    [Theory]
    // The bottom edge reaches the next row's centre (y = 160) after 60 px; the tile's right edge then covers column 0's centre only.
    [InlineData(0, 0, 59, 0)]
    [InlineData(0, 0, 61, 3)]
    [InlineData(1, 0, 61, 4)]
    [InlineData(2, 0, 61, 5)]
    // Dragged down and to the left, the tile takes the slot under its leading corner.
    [InlineData(2, -220, 61, 3)]
    // A partly filled last row only offers the slots it has.
    [InlineData(4, 0, 121, 6)]
    [InlineData(5, 0, 121, 6)]
    public void TargetIndex_moves_down_a_row_once_the_bottom_edge_passes_the_next_rows_centres(int from, double dx, double dy, int expected)
    {
        GameTileDragLayout.TargetIndex(Slots, from, Dragged(from, dx, dy)).Should().Be(expected);
    }

    [Theory]
    // The top edge passing the row above's centre (y = 50) takes 60 px of travel up.
    [InlineData(4, 0, -59, 4)]
    [InlineData(4, 0, -61, 1)]
    [InlineData(3, 0, -61, 0)]
    [InlineData(5, 0, -61, 2)]
    [InlineData(6, 0, -61, 3)]
    [InlineData(6, 0, -500, 0)]
    // Dragged up and to the right from the start of a row lands on the row above, at the tile the left edge reaches.
    [InlineData(3, 220, -61, 2)]
    public void TargetIndex_moves_up_a_row_once_the_top_edge_passes_the_previous_rows_centres(int from, double dx, double dy, int expected)
    {
        GameTileDragLayout.TargetIndex(Slots, from, Dragged(from, dx, dy)).Should().Be(expected);
    }

    [Fact]
    public void TargetIndex_of_a_single_tile_is_itself()
    {
        GameTileDragLayout.TargetIndex([new Rect(0, 0, 100, 100)], 0, new Rect(300, 300, 100, 100)).Should().Be(0);
    }

    [Theory]
    // Moving 1 -> 4 the tiles in between each take the slot before theirs, wrapping from the start of a row to the end of the one above.
    [InlineData(2, 1, 4, -110, 0)]
    [InlineData(3, 1, 4, 220, -110)]
    [InlineData(4, 1, 4, -110, 0)]
    [InlineData(5, 1, 4, 0, 0)]
    [InlineData(0, 1, 4, 0, 0)]
    // Moving 4 -> 1 they each take the slot after theirs.
    [InlineData(1, 4, 1, 110, 0)]
    [InlineData(2, 4, 1, -220, 110)]
    [InlineData(3, 4, 1, 110, 0)]
    [InlineData(5, 4, 1, 0, 0)]
    // The dragged tile is positioned by the pointer, not by this.
    [InlineData(1, 1, 4, 0, 0)]
    // Nothing shifts when the tile hasn't left its slot.
    [InlineData(2, 1, 1, 0, 0)]
    public void NeighbourShift_slides_each_displaced_tile_into_its_neighbours_slot(int index, int from, int target, double dx, double dy)
    {
        GameTileDragLayout.NeighbourShift(Slots, index, from, target).Should().Be(new Vector(dx, dy));
    }

    [Theory]
    [InlineData(1, 4, 0, 110)]
    [InlineData(4, 1, 0, -110)]
    [InlineData(2, 6, -220, 220)]
    [InlineData(3, 3, 0, 0)]
    public void SettleOffset_carries_the_dragged_tile_to_the_target_slot(int from, int target, double dx, double dy)
    {
        GameTileDragLayout.SettleOffset(Slots, from, target).Should().Be(new Vector(dx, dy));
    }

    [Fact]
    public void ClampOffset_keeps_the_dragged_tile_inside_the_grid()
    {
        // The grid spans x 0..320 and y 0..320; tile 4 sits at (110, 110).
        GameTileDragLayout.ClampOffset(Slots, 4, new Vector(-900, -900)).Should().Be(new Vector(-110, -110));
        GameTileDragLayout.ClampOffset(Slots, 4, new Vector(900, 900)).Should().Be(new Vector(110, 110));
        GameTileDragLayout.ClampOffset(Slots, 4, new Vector(7, -9)).Should().Be(new Vector(7, -9));
    }
}
