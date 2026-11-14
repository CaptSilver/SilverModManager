using R3;

namespace SilverModManager.App.UI.Input;

/// <summary>Which device the user is steering with: a pointer shows hover and tooltips, a pad shows the focus ring and hints.</summary>
public enum InputMode
{
    Pointer,
    Pad,
}

public interface IInputModeService
{
    InputMode Mode { get; }

    /// <summary>The current mode first, then each change.</summary>
    Observable<InputMode> Modes { get; }

    /// <summary>
    /// Popups draw inside the window instead of in windows of their own: always in Game Mode, whose compositor has no
    /// window manager to place them, and in Pad mode, where a separate popup window would take the focus the pad is driving.
    /// </summary>
    bool UsesOverlayPopups { get; }

    /// <summary>A pad did something the window acted on.</summary>
    void PadUsed();

    /// <summary>The pointer moved over the window.</summary>
    void PointerMoved();
}
