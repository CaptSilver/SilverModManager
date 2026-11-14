using Avalonia.Controls;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>RB and LB select the next and previous tab of the tab control around the focus. They stop at the ends.</summary>
public sealed class TabCycleBinding(bool forward) : PadBinding
{
    public override GamepadButton Button => forward ? GamepadButton.RB : GamepadButton.LB;

    public override string Label => forward ? "Next tab" : "Previous tab";

    public override bool CanExecute(FocusContext ctx) => TargetOf(ctx) is not null;

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        if (TargetOf(ctx) is var (tabs, index)) tabs.SelectedIndex = index;
    }

    private (TabControl Tabs, int Index)? TargetOf(FocusContext ctx)
    {
        if (FocusAncestors.Nearest<TabControl>(ctx.Focused) is not { } tabs) return null;

        var target = tabs.SelectedIndex + (forward ? 1 : -1);
        return target >= 0 && target < tabs.ItemCount ? (tabs, target) : null;
    }
}
