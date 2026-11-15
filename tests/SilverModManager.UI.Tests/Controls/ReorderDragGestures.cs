using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>Pointer gestures the reorder-drag tests send to a window. Everything runs on the UI thread.</summary>
internal static class ReorderDragGestures
{
    public static Point CentreIn(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>Presses at <paramref name="from"/>, moves in small steps to <paramref name="to"/> and releases there.</summary>
    public static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Presses at <paramref name="from"/> and moves to <paramref name="to"/>, leaving the button down.</summary>
    public static void PressAndMove(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Move(window, from, to);
    }

    private static void Move(Window window, Point from, Point to)
    {
        for (var step = 1; step <= 8; step++)
        {
            window.MouseMove(from + (to - from) * step / 8, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
