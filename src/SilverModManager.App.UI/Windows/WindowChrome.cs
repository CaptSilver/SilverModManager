using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// Hit testing for undecorated windows (the main window and dialogs). Without a window manager title bar and border,
/// the app has to tell its own chrome clicks apart: which press starts a move, and which
/// border position starts a resize.
/// </summary>
internal static class WindowChrome
{
    // Pointer moves fire constantly; reuse the cursors instead of creating a platform cursor per move.
    private static readonly Cursor NorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor WestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor TopLeftCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightCursor = new(StandardCursorType.TopRightCorner);

    /// <summary>
    /// Gives an undecorated window resize grips along its border: the matching resize cursor on hover
    /// and a resize drag on a left press. Does nothing for a window that is not resizable.
    /// </summary>
    public static void AttachResizeGrips(Window window)
    {
        WindowEdge? EdgeAt(PointerEventArgs e) =>
            window.CanResize ? GetResizeEdge(e.GetPosition(window), window.Bounds.Size, window.WindowState) : null;

        window.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            window.Cursor = EdgeAt(e) switch
            {
                WindowEdge.North or WindowEdge.South => NorthSouthCursor,
                WindowEdge.West or WindowEdge.East => WestEastCursor,
                WindowEdge.NorthWest or WindowEdge.SouthEast => TopLeftCursor,
                WindowEdge.NorthEast or WindowEdge.SouthWest => TopRightCursor,
                _ => null,
            };
        }, RoutingStrategies.Tunnel);

        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (EdgeAt(e) is not { } edge) return;

            window.BeginResizeDrag(edge, e);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Width of the invisible resize band along each window edge.</summary>
    public const double GripThickness = 5;

    /// <summary>Length of the corner zones, measured along both edges from the corner.</summary>
    public const double CornerLength = 12;

    /// <summary>
    /// The edge or corner a press at <paramref name="point"/> would resize, or null when it
    /// falls outside the grips. Grips are inactive unless the window is in its normal state,
    /// since a maximized or fullscreen window has no border to drag.
    /// </summary>
    public static WindowEdge? GetResizeEdge(Point point, Size windowSize, WindowState state)
    {
        if (state != WindowState.Normal) return null;
        if (point.X < 0 || point.Y < 0 || point.X > windowSize.Width || point.Y > windowSize.Height) return null;

        var nearLeft = point.X < GripThickness;
        var nearRight = point.X > windowSize.Width - GripThickness;
        var nearTop = point.Y < GripThickness;
        var nearBottom = point.Y > windowSize.Height - GripThickness;
        if (!nearLeft && !nearRight && !nearTop && !nearBottom) return null;

        // A press inside a corner zone resizes both axes, even if it is only inside the grip on one.
        var cornerLeft = point.X < CornerLength;
        var cornerRight = point.X > windowSize.Width - CornerLength;
        var cornerTop = point.Y < CornerLength;
        var cornerBottom = point.Y > windowSize.Height - CornerLength;

        if ((nearTop || cornerTop) && (nearLeft || cornerLeft) && (nearTop || nearLeft)) return WindowEdge.NorthWest;
        if ((nearTop || cornerTop) && (nearRight || cornerRight) && (nearTop || nearRight)) return WindowEdge.NorthEast;
        if ((nearBottom || cornerBottom) && (nearLeft || cornerLeft) && (nearBottom || nearLeft)) return WindowEdge.SouthWest;
        if ((nearBottom || cornerBottom) && (nearRight || cornerRight) && (nearBottom || nearRight)) return WindowEdge.SouthEast;

        if (nearTop) return WindowEdge.North;
        if (nearBottom) return WindowEdge.South;
        if (nearLeft) return WindowEdge.West;
        return WindowEdge.East;
    }

    /// <summary>
    /// True when a press that landed on <paramref name="source"/> hit bare top bar surface rather
    /// than something the user can interact with. Anything on the path from the source up to
    /// <paramref name="boundary"/> that is a button, text, a text box, or otherwise focusable
    /// (menu items, tabs, dropdowns) keeps the press for itself.
    /// </summary>
    public static bool IsEmptyDragSurface(Visual? source, Visual boundary)
    {
        if (source is null) return false;

        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (IsInteractive(current)) return false;
            if (ReferenceEquals(current, boundary)) return true;
        }

        // Walked to the root without meeting the boundary: the press was elsewhere in the window.
        return false;
    }

    private static bool IsInteractive(Visual visual) => visual is
        Avalonia.Controls.Primitives.Thumb or
        Avalonia.Controls.Button or
        TextBlock or
        TextBox or
        InputElement { Focusable: true };
}
