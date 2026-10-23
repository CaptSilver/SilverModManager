using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App;

/// <summary>
/// Turns a stop of the generic host (SIGTERM, SIGINT and SIGQUIT arrive there through its console lifetime)
/// into a shutdown of the Avalonia desktop lifetime. Without it the host cancels the signal's default exit,
/// nothing else listens, and the window keeps running.
/// </summary>
public sealed class DesktopShutdownOnHostStop(IHostApplicationLifetime hostLifetime, ILogger<DesktopShutdownOnHostStop> logger)
{
    /// <summary>
    /// Starts watching the host. <paramref name="postToUiThread"/> defaults to the Avalonia dispatcher; the signal
    /// callback runs on a thread-pool thread, and the desktop lifetime has to be shut down from the UI thread.
    /// </summary>
    public void Attach(IClassicDesktopStyleApplicationLifetime desktop, Action<Action>? postToUiThread = null)
    {
        var post = postToUiThread ?? (action => Dispatcher.UIThread.Post(action));
        var requested = 0;

        // A window close also stops the host, but only after the UI loop has ended; by then there is nothing left to shut down.
        desktop.Exit += (_, _) => Interlocked.Exchange(ref requested, 1);

        hostLifetime.ApplicationStopping.Register(() =>
        {
            // Repeated signals must not queue a second shutdown behind the first.
            if (Interlocked.Exchange(ref requested, 1) != 0) return;

            logger.LogInformation("The host is stopping, closing the application like a window close");
            // TryShutdown raises ShutdownRequested and closes the windows, the same route as the main window's close.
            post(() => desktop.TryShutdown());
        });
    }
}
