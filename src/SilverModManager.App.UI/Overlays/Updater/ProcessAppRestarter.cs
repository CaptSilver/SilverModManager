using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.UI.Overlays.Updater;

/// <summary>
/// Starts the app again after an update.
/// </summary>
internal interface IAppRestarter
{
    /// <summary>
    /// Starts <paramref name="appImagePath"/> with the arguments of the running instance once this one has exited, then shuts this one down.
    /// </summary>
    void Restart(string appImagePath);
}

internal sealed class ProcessAppRestarter(ILogger<ProcessAppRestarter> logger) : IAppRestarter
{
    // The AppImage runtime sets these for the instance it launches; a copy pointing at the old mount would confuse the new one.
    private static readonly string[] AppImageVariables = ["APPIMAGE", "APPDIR", "OWD", "ARGV0"];

    // A window started while this one still holds the database fails on its lock, so the shell waits for this process
    // to exit (database closed, main slot released) and then becomes the new app. $1 is the pid, the rest the command.
    private const string WaitThenExec = "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; shift; exec \"$@\"";

    public void Restart(string appImagePath)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            ?? throw new InvalidOperationException("There is no desktop lifetime to shut down, refusing to start a second instance.");

        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);

        var startInfo = BuildStartInfo(appImagePath, Environment.GetCommandLineArgs().Skip(1), environment, Environment.ProcessId);
        logger.LogInformation("Starting the updated app `{AppImage}`", appImagePath);
        var waiter = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start `{appImagePath}`.");

        logger.LogInformation("Shutting down the old instance");
        Dispatcher.UIThread.Post(() =>
        {
            using (waiter)
            {
                if (lifetime.TryShutdown()) return;

                // Left alone, the waiter would start the new version whenever the app is quit later.
                logger.LogWarning("The shutdown was cancelled, not starting the updated app");
                waiter.Kill();
            }
        });
    }

    internal static ProcessStartInfo BuildStartInfo(string appImagePath, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?> environment, int waitForPid)
    {
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in (string[])["-c", WaitThenExec, "sh", waitForPid.ToString(CultureInfo.InvariantCulture), appImagePath]) info.ArgumentList.Add(argument);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        info.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (AppImageVariables.Contains(key)) continue;
            info.Environment[key] = value;
        }
        return info;
    }
}
