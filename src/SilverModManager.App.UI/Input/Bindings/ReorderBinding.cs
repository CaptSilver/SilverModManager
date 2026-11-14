using SilverModManager.App.UI.Input.Reorder;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>
/// Y picks up the focused row of an ordering page. While it is lifted the D-pad moves it a place per press, the
/// triggers a page, A (or Y again) drops it and B puts it back. One binding per button, because the hint bar names
/// one thing per button; they share the <see cref="ReorderState"/>.
/// </summary>
public sealed class ReorderBinding(ReorderStep action, ReorderState state) : PadBinding, IHintSource, IPadBinding
{
    // One binding reports for all of them: the hint bar needs a single redraw when the lift changes.
    public event Action? HintsChanged
    {
        add { if (action == ReorderStep.PickUp) state.Changed += value; }
        remove { if (action == ReorderStep.PickUp) state.Changed -= value; }
    }

    public override GamepadButton Button => action switch
    {
        ReorderStep.PickUp => GamepadButton.Y,
        ReorderStep.Up => GamepadButton.DPadUp,
        ReorderStep.Down => GamepadButton.DPadDown,
        ReorderStep.PageUp => GamepadButton.LT,
        ReorderStep.PageDown => GamepadButton.RT,
        ReorderStep.Drop => GamepadButton.A,
        _ => GamepadButton.B,
    };

    public override string Label => action switch
    {
        ReorderStep.PickUp => "Reorder",
        ReorderStep.Up or ReorderStep.Down => "Move",
        ReorderStep.PageUp or ReorderStep.PageDown => "Move a page",
        ReorderStep.Drop => "Drop",
        _ => "Put back",
    };

    public override string Hint(FocusContext ctx)
    {
        if (!state.IsLive || state.Session is not { } session) return action == ReorderStep.PickUp ? "Reorder" : Label;

        return action switch
        {
            // Y drops too; showing it beside A would repeat the same entry.
            ReorderStep.PickUp => "",
            ReorderStep.Up or ReorderStep.Down => $"Move (position {session.TargetIndex + 1} of {state.Count})",
            _ => Label,
        };
    }

    protected override bool Repeats => action is ReorderStep.Up or ReorderStep.Down;

    public override bool CanExecute(FocusContext ctx) => action switch
    {
        ReorderStep.PickUp => state.IsLive || (ReorderSources.For(ctx.Focused) is { } source && ctx.Focused is { } focused && source.CanBegin(focused)),
        _ => state.IsLive,
    };

    public override void Execute(GamepadEvent e, FocusContext ctx)
    {
        switch (action)
        {
            case ReorderStep.PickUp when state.IsLive:
            case ReorderStep.Drop:
                _ = state.Drop();
                break;
            case ReorderStep.PickUp:
                if (ReorderSources.For(ctx.Focused) is { } source && ctx.Focused is { } focused) state.Begin(source, focused);
                break;
            case ReorderStep.Up: state.Nudge(-1); break;
            case ReorderStep.Down: state.Nudge(+1); break;
            case ReorderStep.PageUp: state.Nudge(-state.PageSize); break;
            case ReorderStep.PageDown: state.Nudge(+state.PageSize); break;
            case ReorderStep.Cancel: _ = state.Cancel(); break;
        }
    }

    void IPadBinding.WindowDeactivated()
    {
        // A press made in a game must not drop a row when the window comes back, so the lift is put back instead.
        if (action == ReorderStep.Cancel && state.IsLive) _ = state.Cancel();
    }
}

/// <summary>What a pad press does to a lifted row, or picks it up.</summary>
public enum ReorderStep { PickUp, Up, Down, PageUp, PageDown, Drop, Cancel }

/// <summary>
/// Swallows every other press while a row is lifted, so the same button does not also move focus or open the menu
/// behind it. It has no label, so the hint bar leaves it out.
/// </summary>
public sealed class ReorderGuardBinding(ReorderState state) : IPadBinding
{
    public GamepadButton Button => GamepadButton.Menu;

    public string Label => "";

    public bool Matches(GamepadEvent e, FocusContext ctx) => e is ButtonDown or ButtonRepeat or Stick { Side: StickSide.Left, IsDeflected: true };

    public bool CanExecute(FocusContext ctx) => state.IsLive;

    public void Execute(GamepadEvent e, FocusContext ctx)
    {
    }
}
