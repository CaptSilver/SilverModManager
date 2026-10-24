namespace SilverModManager.App.UI.Controls.Spine;

/// <summary>
/// The arithmetic behind dragging a loadout tile along the spine: which slot the lifted tile
/// is over and how far the others slide to open it. Kept free of controls so it can be tested.
/// </summary>
internal static class SpineDragLayout
{
    /// <summary>Pointer travel, in either axis, below which a press is still a click.</summary>
    public const double DragThreshold = 4;

    public static bool ExceedsThreshold(double deltaX, double deltaY) =>
        Math.Abs(deltaX) >= DragThreshold || Math.Abs(deltaY) >= DragThreshold;

    /// <summary>
    /// The index the dragged tile would take if released with its centre at <paramref name="draggedCentreY"/>.
    /// A neighbour gives way once the centre passes its midpoint.
    /// </summary>
    public static int TargetIndex(IReadOnlyList<double> tops, IReadOnlyList<double> heights, int from, double draggedCentreY)
    {
        var target = from;
        for (var i = from + 1; i < tops.Count; i++)
        {
            if (draggedCentreY > tops[i] + heights[i] / 2) target = i;
        }
        for (var i = from - 1; i >= 0; i--)
        {
            if (draggedCentreY < tops[i] + heights[i] / 2) target = i;
        }
        return target;
    }

    /// <summary>
    /// How far tile <paramref name="index"/> sits from its resting place while tile <paramref name="from"/>
    /// is hovering over <paramref name="target"/>. <paramref name="stride"/> is the dragged tile's height plus the gap.
    /// </summary>
    public static double NeighbourShift(int index, int from, int target, double stride)
    {
        if (index == from) return 0;
        if (index > from && index <= target) return -stride;
        if (index < from && index >= target) return stride;
        return 0;
    }

    /// <summary>The offset from its resting place at which the dragged tile fills the slot at <paramref name="target"/>.</summary>
    public static double SettleOffset(IReadOnlyList<double> tops, IReadOnlyList<double> heights, int from, int target)
    {
        if (target == from) return 0;
        // Moving down, the tile ends flush with the bottom of the tile it displaced; moving up, with its top.
        return target > from
            ? tops[target] + heights[target] - (tops[from] + heights[from])
            : tops[target] - tops[from];
    }

    /// <summary>Keeps the dragged tile inside the span of the list.</summary>
    public static double ClampOffset(IReadOnlyList<double> tops, IReadOnlyList<double> heights, int from, double offset)
    {
        var min = tops[0] - tops[from];
        var last = tops.Count - 1;
        var max = tops[last] + heights[last] - (tops[from] + heights[from]);
        return Math.Clamp(offset, min, max);
    }
}
