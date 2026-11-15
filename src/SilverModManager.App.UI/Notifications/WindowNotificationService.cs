using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using SilverModManager.Sdk.Errors;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Notifications;

public class WindowNotificationService : IWindowNotificationService
{
    private WindowNotificationManager? _notificationManager;

    /// <summary>
    /// Lazy initialization, as main window may not available at creation time
    /// Needs to be called on UI thread
    /// </summary>
    private WindowNotificationManager? GetNotificationManager()
    {
        if (_notificationManager != null)
            return _notificationManager;

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: not null } desktopLifetime)
        {
            // Unable to access the main window, so we cannot show notifications
            return null;
        }
        
        // Must be on UI thread to create the WindowNotificationManager
        _notificationManager = new WindowNotificationManager(desktopLifetime.MainWindow)
        {
            Position = NotificationPosition.BottomCenter,
            MaxItems = 4,
        };
            
        return _notificationManager;
    }

    /// <Inheritdoc />
    public void ShowToast(
        string message,
        ToastNotificationVariant type = ToastNotificationVariant.Neutral,
        TimeSpan? expiration = null)
    {
        DispatcherHelper.EnsureOnUIThread(() =>
            {
                var manager = GetNotificationManager();
                if (manager == null) return;
                
                // Must be on UI thread to show the notification
                manager.Show(BuildNotification(message, expiration, type));
                return;
            }
        );
    }

    /// <summary>
    /// The toast shows plain text, but messages arrive as Markdown (a refusal wraps each name in a code span, which
    /// the dialogs render). Code spans are unwrapped and escapes removed here, once, for every caller; a name stays
    /// inert because plain text has nothing to interpret.
    /// </summary>
    internal static Notification BuildNotification(string message, TimeSpan? expiration, ToastNotificationVariant variant = ToastNotificationVariant.Neutral) =>
        new(null, MarkdownText.PlainText(MarkdownText.ToPlain(message), keepLayout: true), ToNotificationType(variant), expiration ?? TimeSpan.FromSeconds(5));

    /// <summary>
    /// The card theme colours a toast by its <see cref="NotificationType"/>, so this is what makes a failure look different from a success.
    /// </summary>
    internal static NotificationType ToNotificationType(ToastNotificationVariant variant) => variant switch
    {
        ToastNotificationVariant.Neutral => NotificationType.Information,
        ToastNotificationVariant.Success => NotificationType.Success,
        ToastNotificationVariant.Failure => NotificationType.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };
}
