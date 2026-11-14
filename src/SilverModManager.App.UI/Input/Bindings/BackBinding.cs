using Avalonia.Input;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>B closes the open overlay, or sends Escape when there is none, which closes a popup or backs out of a dialog.</summary>
public sealed class BackBinding : PadBinding
{
    public override GamepadButton Button => GamepadButton.B;

    public override string Label => "Back";

    public override string Hint(FocusContext ctx) => ctx.Overlays.CurrentOverlay is null ? Label : "Close";

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        if (ctx.Overlays.CurrentOverlay is { } overlay)
        {
            ctx.Overlays.Remove(overlay);
            return;
        }

        PadKeys.Press(ctx.Focused ?? ctx.Root, Key.Escape);
    }
}
