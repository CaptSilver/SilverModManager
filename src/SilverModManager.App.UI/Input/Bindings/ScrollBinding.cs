using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>
/// The right stick scrolls the scroll viewer around the focused control, and the triggers page it. The stick reports only
/// when it moves, so a held deflection keeps scrolling from a timer until it comes back to the centre.
/// </summary>
public sealed class ScrollBinding : IPadBinding
{
    private const double PixelsPerSecond = 1400;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);

    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private ScrollViewer? _scrolling;
    private double _velocity;

    public ScrollBinding()
    {
        _timer.Tick += (_, _) => Step();
    }

    public GamepadButton Button => GamepadButton.RightStick;

    public string Label => "Scroll";

    /// <summary>Whether the stick is still scrolling from the timer.</summary>
    internal bool IsScrolling => _timer.IsEnabled;

    public void WindowDeactivated()
    {
        _velocity = 0;
        _timer.Stop();
    }

    public bool Matches(GamepadEvent e, FocusContext ctx) => e switch
    {
        Stick { Side: StickSide.Right } => true,
        ButtonDown { Button: GamepadButton.LT or GamepadButton.RT } => true,
        _ => false,
    };

    public bool CanExecute(FocusContext ctx) => ScrollerOf(ctx) is not null;

    public void Execute(GamepadEvent e, FocusContext ctx)
    {
        if (ScrollerOf(ctx) is not { } scroller) return;

        switch (e)
        {
            case Stick stick:
                _scrolling = scroller;
                _velocity = Math.Abs(stick.Y) >= Stick.DeflectionThreshold ? stick.Y * PixelsPerSecond : 0;
                if (_velocity == 0)
                {
                    _timer.Stop();
                    break;
                }

                Step();
                _timer.Start();
                break;
            case ButtonDown { Button: GamepadButton.RT }: scroller.PageDown(); break;
            case ButtonDown { Button: GamepadButton.LT }: scroller.PageUp(); break;
        }
    }

    private void Step()
    {
        if (_scrolling is null || _velocity == 0)
        {
            _timer.Stop();
            return;
        }

        var offset = _scrolling.Offset;
        _scrolling.Offset = new Vector(offset.X, Math.Max(0, offset.Y + _velocity * Interval.TotalSeconds));
    }

    /// <summary>The nearest scroll viewer above the focus that has something to scroll, or the window's first one when nothing is focused.</summary>
    private static ScrollViewer? ScrollerOf(FocusContext ctx)
    {
        var scrollers = FocusAncestors.Of(ctx.Focused).OfType<ScrollViewer>();
        return scrollers.FirstOrDefault(s => s.Extent.Height > s.Viewport.Height)
               ?? Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(ctx.Root).OfType<ScrollViewer>().FirstOrDefault(s => s.Extent.Height > s.Viewport.Height);
    }
}
