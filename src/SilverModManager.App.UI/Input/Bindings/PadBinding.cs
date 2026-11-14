using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input.Bindings;

/// <summary>A binding for one button: it matches that button's press, and its auto-repeat when <see cref="Repeats"/> is set.</summary>
public abstract class PadBinding : IPadBinding
{
    public abstract GamepadButton Button { get; }

    public abstract string Label { get; }

    public virtual string Hint(FocusContext ctx) => Label;

    protected virtual bool Repeats => false;

    public virtual bool Matches(GamepadEvent e, FocusContext ctx) => e switch
    {
        ButtonDown down => down.Button == Button,
        ButtonRepeat repeat => Repeats && repeat.Button == Button,
        _ => false,
    };

    public virtual bool CanExecute(FocusContext ctx) => true;

    public abstract void Execute(GamepadEvent e, FocusContext ctx);
}
