using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Sdk;

namespace SilverModManager.StandardGameLocators.TestHelpers;

/// <summary>
/// Stands in for the notify-send backed notifier: the test runner keeps the D-Bus session,
/// so a real notification from a test would pop up on the desktop.
/// </summary>
public sealed class RecordingDesktopNotifier : IDesktopNotifier
{
    private readonly List<(string Summary, string Body)> _notifications = [];

    public IReadOnlyList<(string Summary, string Body)> Notifications
    {
        get { lock (_notifications) return _notifications.ToArray(); }
    }

    /// <summary>Runs on every notification, so a test can see what else had happened when it was sent.</summary>
    public Action<string, string>? OnNotify { get; set; }

    public void Notify(string summary, string body)
    {
        lock (_notifications) _notifications.Add((summary, body));
        OnNotify?.Invoke(summary, body);
    }
}

public static class RecordingDesktopNotifierExtensions
{
    /// <summary>
    /// Replaces whatever <see cref="IDesktopNotifier"/> is registered with a <see cref="RecordingDesktopNotifier"/>.
    /// </summary>
    public static IServiceCollection ReplaceDesktopNotifier(this IServiceCollection services)
    {
        return services.Replace(ServiceDescriptor.Singleton<IDesktopNotifier, RecordingDesktopNotifier>());
    }
}
