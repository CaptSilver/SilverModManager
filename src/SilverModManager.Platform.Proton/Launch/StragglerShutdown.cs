using System.Diagnostics;
using System.Runtime.InteropServices;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Ends whatever the process tracker still attributes to a game installation: Wine servers, Steam's reaper or the
/// game itself can outlive the process that started them. Shared by a game we spawned and one Steam started.
/// </summary>
internal static partial class StragglerShutdown
{
    private const int SigTerm = 15;
    private const int SigKill = 9;

    /// <summary>
    /// Gives the processes the grace to leave on their own, then SIGTERM, then SIGKILL, each with the grace.
    /// Returns a process that survived SIGKILL, or null when the game is gone.
    /// </summary>
    public static async Task<TrackedProcess?> RunAsync(IProcessTracker tracker, GameInstallation installation, TimeSpan grace, CancellationToken cancellationToken)
    {
        var remaining = await WaitUntilGoneAsync(tracker, installation, grace, signal: null, cancellationToken);
        if (remaining.Count > 0) remaining = await WaitUntilGoneAsync(tracker, installation, grace, SigTerm, cancellationToken);
        if (remaining.Count > 0) remaining = await WaitUntilGoneAsync(tracker, installation, grace, SigKill, cancellationToken);
        return remaining.Count > 0 ? remaining[0] : null;
    }

    private static async Task<IReadOnlyList<TrackedProcess>> WaitUntilGoneAsync(
        IProcessTracker tracker, GameInstallation installation, TimeSpan grace, int? signal, CancellationToken cancellationToken)
    {
        var processes = FindStragglers(tracker, installation);
        if (signal is { } number)
        {
            foreach (var process in processes) kill(process.Pid, number);
        }

        var deadline = Stopwatch.StartNew();
        while (processes.Count > 0 && deadline.Elapsed < grace)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            processes = FindStragglers(tracker, installation);
        }
        return processes;
    }

    private static IReadOnlyList<TrackedProcess> FindStragglers(IProcessTracker tracker, GameInstallation installation)
        => tracker.FindGameProcesses(installation).Where(process => GameProcess.IsSafeToSignal(process.Pid)).ToList();

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
