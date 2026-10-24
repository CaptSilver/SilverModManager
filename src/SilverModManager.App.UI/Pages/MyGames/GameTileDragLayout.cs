using Avalonia;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// The arithmetic behind dragging a game tile around the My Games grid: which slot the lifted tile is over and
/// where the others slide to open it. Slots are the tiles' resting rectangles in reading order (left to right,
/// then down). Kept free of controls so it can be tested.
/// </summary>
internal static class GameTileDragLayout
{
    /// <summary>
    /// The index the dragged tile would take if released where <paramref name="dragged"/> is. A neighbour gives way once the
    /// tile's leading edge passes its centre: the right and bottom edges when moving on in the order, the left and top
    /// edges when moving back.
    /// </summary>
    public static int TargetIndex(IReadOnlyList<Rect> slots, int from, Rect dragged)
    {
        var rows = RowIndices(slots);
        var rowCentreY = RowCentres(slots, rows);

        // The last row whose centre the bottom edge has passed, and the first row whose centre the top edge hasn't.
        var leadRow = 0;
        for (var row = 0; row < rowCentreY.Count; row++)
        {
            if (rowCentreY[row] < dragged.Bottom) leadRow = row;
        }
        var trailRow = rowCentreY.Count - 1;
        for (var row = rowCentreY.Count - 1; row >= 0; row--)
        {
            if (rowCentreY[row] > dragged.Top) trailRow = row;
        }

        var target = from;
        for (var i = from + 1; i < slots.Count; i++)
        {
            if (rows[i] < leadRow || (rows[i] == leadRow && dragged.Right > slots[i].Center.X)) target = i;
        }
        if (target != from) return target;

        for (var i = from - 1; i >= 0; i--)
        {
            if (rows[i] > trailRow || (rows[i] == trailRow && dragged.Left < slots[i].Center.X)) target = i;
        }
        return target;
    }

    /// <summary>
    /// How far tile <paramref name="index"/> sits from its resting place while tile <paramref name="from"/> is
    /// hovering over <paramref name="target"/>: each displaced tile takes the slot next to its own, which may be at the other
    /// end of the row above or below.
    /// </summary>
    public static Vector NeighbourShift(IReadOnlyList<Rect> slots, int index, int from, int target)
    {
        if (index == from) return default;
        if (index > from && index <= target) return slots[index - 1].TopLeft - slots[index].TopLeft;
        if (index < from && index >= target) return slots[index + 1].TopLeft - slots[index].TopLeft;
        return default;
    }

    /// <summary>The offset from its resting place at which the dragged tile fills the slot at <paramref name="target"/>.</summary>
    public static Vector SettleOffset(IReadOnlyList<Rect> slots, int from, int target) =>
        slots[target].TopLeft - slots[from].TopLeft;

    /// <summary>Keeps the dragged tile within the bounds of the grid.</summary>
    public static Vector ClampOffset(IReadOnlyList<Rect> slots, int from, Vector offset)
    {
        var bounds = slots[0];
        foreach (var slot in slots) bounds = bounds.Union(slot);

        var tile = slots[from];
        return new Vector(
            Math.Clamp(offset.X, bounds.Left - tile.Left, bounds.Right - tile.Right),
            Math.Clamp(offset.Y, bounds.Top - tile.Top, bounds.Bottom - tile.Bottom)
        );
    }

    // A new row starts wherever the wrapping panel started one: a tile that is no further right than the one before it.
    private static int[] RowIndices(IReadOnlyList<Rect> slots)
    {
        var rows = new int[slots.Count];
        for (var i = 1; i < slots.Count; i++)
        {
            rows[i] = rows[i - 1] + (slots[i].Left <= slots[i - 1].Left ? 1 : 0);
        }
        return rows;
    }

    private static List<double> RowCentres(IReadOnlyList<Rect> slots, int[] rows)
    {
        var centres = new List<double>();
        for (var i = 0; i < slots.Count; i++)
        {
            if (rows[i] == centres.Count) centres.Add(slots[i].Center.Y);
        }
        return centres;
    }
}
