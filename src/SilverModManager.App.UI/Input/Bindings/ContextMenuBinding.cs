using Avalonia.Controls;
using Avalonia.Interactivity;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>X opens the focused control's context menu, the keyboard's menu key.</summary>
public sealed class ContextMenuBinding : PadBinding
{
    public override GamepadButton Button => GamepadButton.X;

    public override string Label => "Options";

    public override bool CanExecute(FocusContext ctx) => ctx.Focused is Interactive;

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        if (ctx.Focused is not Interactive target) return;

        target.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = target });
    }
}
