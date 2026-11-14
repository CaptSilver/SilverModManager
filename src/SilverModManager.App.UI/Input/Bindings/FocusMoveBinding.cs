using Avalonia.Input;
using Avalonia.Interactivity;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>
/// The D-pad moves focus. It raises the arrow key the way a keyboard would, so Avalonia's own directional navigation
/// (<see cref="XYFocus"/>) picks the next control, and a list or text box that uses the direction handles it first.
/// </summary>
public sealed class FocusMoveBinding : PadBinding
{
    public override GamepadButton Button => GamepadButton.DPadUp;

    public override string Label => "Move";

    protected override bool Repeats => true;

    public override bool Matches(GamepadEvent e, FocusContext ctx) =>
        e switch
        {
            ButtonDown down => DirectionOf(down.Button) is not null,
            ButtonRepeat repeat => DirectionOf(repeat.Button) is not null,
            _ => false,
        };

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        var button = e switch { ButtonDown d => d.Button, ButtonRepeat r => r.Button, _ => throw new ArgumentException("Not a button event", nameof(e)) };
        var (direction, key) = DirectionOf(button) ?? throw new ArgumentException($"{button} is not a direction", nameof(e));

        if (ctx.Focused is not { } focused)
        {
            FocusFirst(ctx.Root);
            return;
        }

        if (PadFocusScopes.For(focused) is { } owned && owned.Scope.WantsDirection(direction) && owned.Owner is Interactive owner)
        {
            // The control that wants the direction acts on it. The handler sits on that control, after its own key
            // handling, and stops an unhandled key reaching the navigation that would move focus away, so the last row
            // of a list does not hand focus to whatever is below it.
            void Absorb(object? sender, KeyEventArgs args) => args.Handled = true;
            owner.AddHandler(InputElement.KeyDownEvent, Absorb, RoutingStrategies.Bubble);
            try { PadKeys.Press(focused, key); }
            finally { owner.RemoveHandler(InputElement.KeyDownEvent, Absorb); }
            return;
        }

        // Raised on the window rather than the focused control: a control such as a closed combo box changes its own
        // selection on an arrow key, which would trap the pad inside it. The window's navigation finds the focused element itself.
        PadKeys.Press(ctx.Root, key);
    }

    /// <summary>Gives the first control that can take focus to a pad that has none yet.</summary>
    private static void FocusFirst(Avalonia.Controls.TopLevel root)
    {
        var first = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)
            .OfType<InputElement>()
            .FirstOrDefault(c => c is { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true, IsTabStop: true });
        first?.Focus(NavigationMethod.Directional);
    }

    private static (NavigationDirection Direction, Key Key)? DirectionOf(GamepadButton button) => button switch
    {
        GamepadButton.DPadUp => (NavigationDirection.Up, Key.Up),
        GamepadButton.DPadDown => (NavigationDirection.Down, Key.Down),
        GamepadButton.DPadLeft => (NavigationDirection.Left, Key.Left),
        GamepadButton.DPadRight => (NavigationDirection.Right, Key.Right),
        _ => null,
    };
}
