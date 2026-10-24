using FluentAssertions;
using SilverModManager.App.UI.Controls.Spine;

namespace SilverModManager.UI.Tests.Controls.Spine;

public class SpineDragLayoutTests
{
    // Five 40 px tiles with 10 px between them: tops 0, 50, 100, 150, 200.
    private const double Height = 40;
    private const double Stride = 50;
    private static readonly double[] Tops = [0, 50, 100, 150, 200];
    private static readonly double[] Heights = [Height, Height, Height, Height, Height];

    // The dragged tile's centre when its top sits at the given offset from where it started.
    private static double Centre(int from, double offset) => Tops[from] + offset + Height / 2;

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 49, 1)]
    [InlineData(1, 51, 2)]
    [InlineData(1, 101, 3)]
    [InlineData(1, 151, 4)]
    [InlineData(1, 500, 4)]
    [InlineData(3, -49, 3)]
    [InlineData(3, -51, 2)]
    [InlineData(3, -101, 1)]
    [InlineData(3, -500, 0)]
    public void TargetIndex_follows_the_tile_centre_past_neighbour_midpoints(int from, double offset, int expected)
    {
        SpineDragLayout.TargetIndex(Tops, Heights, from, Centre(from, offset)).Should().Be(expected);
    }

    [Fact]
    public void Dragging_down_past_neighbours_shifts_each_of_them_up_one_slot()
    {
        var target = SpineDragLayout.TargetIndex(Tops, Heights, 0, Centre(0, 105));

        target.Should().Be(2);
        Shifts(0, target).Should().Equal(0, -Stride, -Stride, 0, 0);
    }

    [Fact]
    public void Dragging_up_past_neighbours_shifts_each_of_them_down_one_slot()
    {
        var target = SpineDragLayout.TargetIndex(Tops, Heights, 4, Centre(4, -105));

        target.Should().Be(2);
        Shifts(4, target).Should().Equal(0, 0, Stride, Stride, 0);
    }

    [Fact]
    public void Dragging_back_to_the_start_restores_every_neighbour()
    {
        var down = SpineDragLayout.TargetIndex(Tops, Heights, 1, Centre(1, 105));
        Shifts(1, down).Should().Contain(s => s != 0);

        var back = SpineDragLayout.TargetIndex(Tops, Heights, 1, Centre(1, 0));
        back.Should().Be(1);
        Shifts(1, back).Should().OnlyContain(s => s == 0);
    }

    [Fact]
    public void The_dragged_tile_itself_is_never_shifted()
    {
        SpineDragLayout.NeighbourShift(2, 2, 4, Stride).Should().Be(0);
        SpineDragLayout.NeighbourShift(2, 2, 0, Stride).Should().Be(0);
    }

    [Fact]
    public void The_release_offset_lands_the_tile_in_the_slot_it_opened()
    {
        // Moving from 0 to 2 lands on the top the third tile had.
        SpineDragLayout.SettleOffset(Tops, Heights, 0, 2).Should().Be(100);
        // Moving 4 to 1 lands on the second tile's top.
        SpineDragLayout.SettleOffset(Tops, Heights, 4, 1).Should().Be(-150);
        SpineDragLayout.SettleOffset(Tops, Heights, 3, 3).Should().Be(0);
    }

    [Fact]
    public void The_drag_offset_is_clamped_to_the_list_with_half_a_tile_of_overhang()
    {
        SpineDragLayout.ClampOffset(Tops, Heights, 1, -500).Should().Be(-50 - Height / 2);
        SpineDragLayout.ClampOffset(Tops, Heights, 1, 500).Should().Be(150 + Height / 2);
        SpineDragLayout.ClampOffset(Tops, Heights, 1, 20).Should().Be(20);
    }

    [Fact]
    public void Cancelling_leaves_the_target_at_the_start_with_no_shifts()
    {
        SpineDragLayout.SettleOffset(Tops, Heights, 2, 2).Should().Be(0);
        Shifts(2, 2).Should().OnlyContain(s => s == 0);
    }

    [Fact]
    public void Crossing_the_threshold_is_measured_in_either_axis_but_a_small_wobble_is_a_click()
    {
        SpineDragLayout.ExceedsThreshold(0, 3.9).Should().BeFalse();
        SpineDragLayout.ExceedsThreshold(-3, 3).Should().BeFalse();
        SpineDragLayout.ExceedsThreshold(0, 4).Should().BeTrue();
        SpineDragLayout.ExceedsThreshold(-4.5, 0).Should().BeTrue();
    }

    private static double[] Shifts(int from, int target) =>
        Enumerable.Range(0, Tops.Length).Select(i => SpineDragLayout.NeighbourShift(i, from, target, Stride)).ToArray();

    [Theory]
    [InlineData(1, 10_000, 4)]
    [InlineData(3, -10_000, 0)]
    [InlineData(0, 10_000, 4)]
    [InlineData(4, -10_000, 0)]
    public void A_drag_held_at_either_end_of_the_list_reaches_the_first_or_last_slot(int from, double pointerOffset, int expected)
    {
        // The view clamps the offset before asking for the target, so the ends must still be reachable after clamping.
        var offset = SpineDragLayout.ClampOffset(Tops, Heights, from, pointerOffset);

        SpineDragLayout.TargetIndex(Tops, Heights, from, Centre(from, offset)).Should().Be(expected);
    }

    [Theory]
    // A 60 px wide list, 250 px tall.
    [InlineData(30, -200, true)]
    [InlineData(30, 1000, true)]
    [InlineData(30, 120, true)]
    [InlineData(-200, 120, false)]
    [InlineData(400, 120, false)]
    public void A_release_commits_wherever_it_is_vertically_and_cancels_only_well_off_to_the_side(double x, double y, bool commits)
    {
        // The tile is clamped to the list, so the slot it shows is the one meant however far up or down the pointer went.
        SpineDragLayout.CommitsOnRelease(listWidth: 60, x, y).Should().Be(commits);
    }
}
