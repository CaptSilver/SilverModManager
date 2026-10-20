using JetBrains.Annotations;

namespace NexusMods.Sdk;

/// <summary>
/// Raises a desktop notification for events the user would otherwise only find in a log,
/// such as a launch that could not start from a Steam launch line with no window to show it in.
/// </summary>
[PublicAPI]
public interface IDesktopNotifier
{
    /// <summary>
    /// Shows a critical-urgency notification. Returns immediately; a notification that
    /// cannot be shown is logged and otherwise ignored.
    /// </summary>
    void Notify(string summary, string body);
}
