using Avalonia.Input;
using Avalonia.Interactivity;

namespace SilverModManager.App.UI.Input;

/// <summary>Raises the key events a pad press stands for, typed as coming from a gamepad so the keyboard-only filters leave them alone.</summary>
internal static class PadKeys
{
    /// <summary>Raises key down and key up for <paramref name="key"/> on <paramref name="target"/>. Returns whether something handled the key down.</summary>
    public static bool Press(IInputElement target, Key key)
    {
        if (target is not Interactive interactive) return false;

        var down = Create(InputElement.KeyDownEvent, key, target);
        interactive.RaiseEvent(down);
        interactive.RaiseEvent(Create(InputElement.KeyUpEvent, key, target));
        return down.Handled;
    }

    private static KeyEventArgs Create(RoutedEvent routedEvent, Key key, IInputElement target) => new()
    {
        RoutedEvent = routedEvent,
        Key = key,
        KeyDeviceType = KeyDeviceType.Gamepad,
        Source = target,
    };
}
