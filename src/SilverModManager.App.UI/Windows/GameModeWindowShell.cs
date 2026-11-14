using Avalonia.Controls;
using Avalonia;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// What the window and the toolkit need to behave inside gamescope: the compositor there has no window manager
/// to place popups or size windows.
/// </summary>
public static class GameModeWindowShell
{
    /// <summary>
    /// Why the Close Steam routes are off in Game Mode.
    /// </summary>
    public const string CloseSteamUnavailableReason =
        "In Game Mode the app runs inside Steam, so closing Steam would close the app too. Do this from Desktop Mode.";

    /// <summary>
    /// Fills the screen, since gamescope shows one window at a time and has no title bar to resize with.
    /// </summary>
    public static void Apply(Window window, bool isGameMode)
    {
        if (isGameMode) window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Draws popups (menus, combo boxes, tooltips) inside the window. Separate popup windows are placed by the
    /// window manager, and gamescope has none to place them.
    /// </summary>
    public static void Apply(X11PlatformOptions options, bool isGameMode)
    {
        if (isGameMode) options.OverlayPopups = true;
    }
}
