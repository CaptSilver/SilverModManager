using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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

internal sealed partial class ProcessAppRestarter(ILogger<ProcessAppRestarter> logger) : IAppRestarter
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

        // The AppImage runtime leaves its mount's keep-alive descriptor open in the app without close-on-exec. Passed on
        // to the new AppImage, it kept the old runtime and its mount of the replaced image alive until the new app quit.
        var inherited = InheritableDescriptors();
        MarkCloseOnExec(inherited);
        logger.LogDebug("Not passing descriptors {Descriptors} to the updated app", inherited);

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

    /// <summary>This process's descriptors above stderr that a started program would inherit.</summary>
    internal static int[] InheritableDescriptors() => Directory.EnumerateFileSystemEntries("/proc/self/fd")
        .Select(path => int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var fd) ? fd : -1)
        .Where(fd => fd > 2 && Fcntl(fd, GetDescriptorFlags, 0) is var flags && flags >= 0 && (flags & CloseOnExec) == 0)
        .ToArray();

    /// <summary>Stops <paramref name="descriptors"/> from passing to programs this process starts; this process keeps them.</summary>
    internal static void MarkCloseOnExec(IEnumerable<int> descriptors)
    {
        foreach (var fd in descriptors)
        {
            var flags = Fcntl(fd, GetDescriptorFlags, 0);
            if (flags >= 0) Fcntl(fd, SetDescriptorFlags, flags | CloseOnExec);
        }
    }

    private const int GetDescriptorFlags = 1; // F_GETFD
    private const int SetDescriptorFlags = 2; // F_SETFD
    private const int CloseOnExec = 1; // FD_CLOEXEC

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int descriptor, int command, int argument);

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
