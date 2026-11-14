using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input;

/// <summary>One thing a pad button does. The navigator runs the first binding that matches and can execute.</summary>
public interface IPadBinding
{
    /// <summary>The button the hint bar shows for this binding.</summary>
    GamepadButton Button { get; }

    /// <summary>What the hint bar says the button does.</summary>
    string Label { get; }

    /// <summary>What the hint bar says for the current focus; by default the same as <see cref="Label"/>.</summary>
    string Hint(FocusContext ctx) => Label;

    /// <summary>
    /// The window stopped being active. A binding that keeps acting between pad events (the scroll timer) stops here,
    /// because the release that would have ended it went to a source that was paused.
    /// </summary>
    void WindowDeactivated() { }

    bool Matches(GamepadEvent e, FocusContext ctx);

    /// <summary>False when the binding has nothing to act on right now, so the next one gets the event and the hint bar leaves it out.</summary>
    bool CanExecute(FocusContext ctx);

    void Execute(GamepadEvent e, FocusContext ctx);
}

/// <summary>A binding whose label depends on state that changes between pad events; the hint bar redraws when it fires.</summary>
public interface IHintSource
{
    event Action? HintsChanged;
}
