using ReactiveUI;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.Spine.Buttons.Image;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>
/// RB and LB switch to the next and previous loadout while the focus is in the spine, the strip of game icons on the left.
/// Bound before the tab binding so a spine inside a tabbed page still gets its own shoulders.
/// </summary>
public sealed class SpineCycleBinding(bool forward) : PadBinding
{
    public override GamepadButton Button => forward ? GamepadButton.RB : GamepadButton.LB;

    public override string Label => forward ? "Next loadout" : "Previous loadout";

    public override bool CanExecute(FocusContext ctx) => TargetOf(ctx) is not null;

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        // The command runs the same navigation as clicking the icon.
        if (TargetOf(ctx) is { } target) target.Click.Execute().Subscribe();
    }

    private IImageButtonViewModel? TargetOf(FocusContext ctx)
    {
        var spine = FocusAncestors.Nearest<IViewFor<ISpineViewModel>>(ctx.Focused)?.ViewModel;
        if (spine is null) return null;

        var items = spine.LoadoutSpineItems;
        var active = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].IsActive) continue;
            active = i;
            break;
        }

        // Nothing active (the home page is showing): RB goes to the first loadout, LB has nowhere to go.
        var target = active < 0 ? (forward ? 0 : -1) : active + (forward ? 1 : -1);
        return target >= 0 && target < items.Count ? items[target] : null;
    }
}
