using System.Reflection;

namespace NexusMods.App.UI.Helpers;

/// <summary>
/// Controls the D-Bus connection Avalonia.FreeDesktop opens on Linux.
/// </summary>
public static class AvaloniaDBus
{
    private const string HelperTypeName = "Avalonia.FreeDesktop.DBusHelper, Avalonia.FreeDesktop";

    // The field really is spelled "Conntection" in Avalonia.
    private const string DefaultConnectionFieldName = "s_defaultConntection";

    /// <summary>
    /// Disposes the shared connection Avalonia uses for portal settings, file dialogs and input methods.
    /// </summary>
    /// <remarks>
    /// Avalonia subscribes to that connection from the UI thread, so each subscription remembers the
    /// UI synchronization context. When the connection drops, Tmds.DBus reports the disconnect to every
    /// subscriber through that context from a thread-pool thread. Once the dispatcher has stopped, the
    /// call throws <see cref="TaskCanceledException"/> on that thread and the process aborts. Call this
    /// on the UI thread while the dispatcher still runs (lifetime exit): disposing the connection first
    /// means no later read failure can reach the stopped dispatcher. Avalonia offers no public hook for
    /// this, so the connection is reached by reflection.
    /// </remarks>
    /// <exception cref="MissingMemberException">Avalonia no longer has the expected internal field.</exception>
    public static void DisposeDefaultConnection()
    {
        var helperType = Type.GetType(HelperTypeName, throwOnError: false)
                         ?? throw new MissingMemberException(HelperTypeName);
        var field = helperType.GetField(DefaultConnectionFieldName, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new MissingFieldException(HelperTypeName, DefaultConnectionFieldName);

        // Null means Avalonia never connected (no session bus, or nothing needed it).
        (field.GetValue(null) as IDisposable)?.Dispose();
    }
}
