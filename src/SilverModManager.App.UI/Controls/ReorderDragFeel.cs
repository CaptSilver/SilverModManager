namespace SilverModManager.App.UI.Controls;

/// <summary>
/// How a reorder drag looks on one surface. The menu's entries are as wide as their column, so they lift less than the
/// spine's square icons; the game grid scrolls a taller page, so its edge band is wider.
/// </summary>
internal sealed record ReorderDragFeel(double LiftScale, double EdgeScrollBand, double EdgeScrollMaxStep)
{
    public static readonly ReorderDragFeel Menu = new(1.04, 48, 14);
    public static readonly ReorderDragFeel Spine = new(1.15, 48, 14);
    public static readonly ReorderDragFeel Grid = new(1.05, 64, 18);
}
