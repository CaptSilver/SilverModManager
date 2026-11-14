using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Notifications;

/// <summary>
/// Shows what a headless process would send to the desktop's notification daemon as a toast in the open window.
/// Game Mode has no notification daemon, and a refusal that comes from pressing Play should appear where the user
/// is already looking.
/// </summary>
public sealed class InAppNotifier(IWindowNotificationService toasts, ILogger<InAppNotifier> logger) : IDesktopNotifier
{
    /// <inheritdoc />
    public void Notify(string summary, string body)
    {
        logger.LogInformation("Showing notification in the window: {Summary}: {Body}", summary, body);

        // Zero expiration keeps the toast until it is dismissed: the user may be looking at the game, not the window.
        Dispatcher.UIThread.Post(() => toasts.ShowToast(
            MarkdownText.Inline($"{summary}: {body}"),
            ToastNotificationVariant.Failure,
            expiration: TimeSpan.Zero));
    }
}
