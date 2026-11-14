using Avalonia.Input;
using SilverModManager.Platform.Gamepad;

namespace SilverModManager.App.UI.Input;

/// <summary>
/// Settles who owns an input when the same press can arrive twice. Steam Input can turn a pad into keyboard arrows
/// while SDL still reports the pad itself, so a D-pad press shows up as an arrow key and a pad event within a few
/// milliseconds of each other. Whichever side was heard first keeps the press for <c>150 ms</c> and the other is dropped.
/// </summary>
public sealed class KeyOwnershipFilter(GamepadPresence pads, GamepadPresence keys)
{
    private static readonly HashSet<Key> NavigationKeys = [Key.Up, Key.Down, Key.Left, Key.Right, Key.Enter, Key.Escape];

    /// <summary>A pad event was acted on.</summary>
    public void PadEvent() => pads.Touched();

    /// <summary>A physical navigation key was pressed in the last 150 ms.</summary>
    public bool KeyRecent => keys.IsRecent;

    /// <summary>
    /// Run on the window's tunnelling key-down. A physical key right after a pad event is the pad's own echo and is
    /// marked handled; any other navigation key is remembered, so the pad's echo of it is dropped in turn. Keys the
    /// app itself raised for the pad carry the gamepad device type and are never filtered.
    /// </summary>
    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyDeviceType != KeyDeviceType.Keyboard || !NavigationKeys.Contains(e.Key)) return;

        if (pads.IsRecent) e.Handled = true;
        else keys.Touched();
    }
}
