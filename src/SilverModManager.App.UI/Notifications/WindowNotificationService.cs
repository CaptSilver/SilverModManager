using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using SilverModManager.Sdk.Errors;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;

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
        TimeSpan? expiration = null,
        DialogButtonDefinition[]? buttonDefinitions = null,
        Action<ButtonDefinitionId>? buttonHandler = null)
    {
        DispatcherHelper.EnsureOnUIThread(() =>
            {
                var manager = GetNotificationManager();
                if (manager == null) return;
                
                // TODO: Use ToastNotificationVariant
                // TODO: Use buttons and handler
        
                // Must be on UI thread to show the notification
                manager.Show(BuildNotification(message, expiration));
                return;
            }
        );
    }

    /// <summary>
    /// The toast shows plain text, but messages arrive as Markdown (a refusal wraps each name in a code span, which
    /// the dialogs render). Code spans are unwrapped and escapes removed here, once, for every caller; a name stays
    /// inert because plain text has nothing to interpret.
    /// </summary>
    internal static Notification BuildNotification(string message, TimeSpan? expiration) =>
        new(null, MarkdownText.PlainText(MarkdownText.ToPlain(message), keepLayout: true), NotificationType.Information, expiration ?? TimeSpan.FromSeconds(5));
}
