using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// Remembers that the user, not the code, asked a window to close. The main window has no system decorations, so
/// its own close button is the only close control, and it ends in <see cref="Window.Close()"/>: Avalonia reports
/// that as a programmatic close, indistinguishable from the app closing the window itself unless the button says so.
/// </summary>
public static class UserWindowClose
{
    private static readonly ConditionalWeakTable<Window, object> Requested = new();

    /// <summary>Marks the next close of <paramref name="window"/> as the user's, then closes it.</summary>
    public static void Request(Window window)
    {
        Requested.AddOrUpdate(window, window);
        window.Close();
    }

    /// <summary>True when the user asked for <paramref name="window"/> to close with one of the app's own controls.</summary>
    public static bool WasRequested(Window window) => Requested.TryGetValue(window, out _);
}
