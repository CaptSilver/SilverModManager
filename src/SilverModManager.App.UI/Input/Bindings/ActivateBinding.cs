using Avalonia.Input;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>A presses Enter on the focused control, which is what activates a button, opens a combo box and toggles a check box.</summary>
public sealed class ActivateBinding : PadBinding
{
    public override GamepadButton Button => GamepadButton.A;

    public override string Label => "Select";

    public override bool CanExecute(FocusContext ctx) => ctx.Focused is not null;

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        if (ctx.Focused is { } focused) PadKeys.Press(focused, Key.Enter);
    }
}
