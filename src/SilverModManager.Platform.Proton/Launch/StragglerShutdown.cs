using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    /// Returns a process that survived SIGKILL, or null when the game is gone. Tools the app started are neither
    /// signalled nor waited on.
    /// </summary>
    /// <param name="protectedPids">
    /// Processes to leave alone, with all their ancestors. A forwarded launch's <c>run</c> process shares the game's
    /// prefix in its environment, so the tracker lists it, yet it is still waiting for the game's exit to be reported.
    /// </param>
    /// <param name="signal">Sends a signal to a pid and returns the call's result. Defaults to <c>kill(2)</c>.</param>
    /// <param name="logger">Receives each signal sent at Trace and each wait at Debug.</param>
    public static async Task<TrackedProcess?> RunAsync(
        IRunningGameDetector detector, GameInstallation installation, TimeSpan grace, CancellationToken cancellationToken,
        IReadOnlySet<int>? protectedPids = null, Func<int, int, int>? signal = null, ILogger? logger = null)
    {
        var sweep = new Sweep(detector, installation, grace, signal ?? kill, protectedPids, logger);
        var remaining = await sweep.WaitUntilGoneAsync(number: null, cancellationToken);
        if (remaining.Count > 0) remaining = await sweep.WaitUntilGoneAsync(SigTerm, cancellationToken);
        if (remaining.Count > 0) remaining = await sweep.WaitUntilGoneAsync(SigKill, cancellationToken);
        return remaining.Count > 0 ? remaining[0] : null;
    }

    private static string SignalName(int number) => number switch { SigTerm => "SIGTERM", SigKill => "SIGKILL", _ => $"signal {number}" };

    private sealed class Sweep(IRunningGameDetector detector, GameInstallation installation, TimeSpan grace, Func<int, int, int> signal, IReadOnlySet<int>? protectedPids, ILogger? logger)
    {
        public async Task<IReadOnlyList<TrackedProcess>> WaitUntilGoneAsync(int? number, CancellationToken cancellationToken)
        {
            var processes = FindStragglers();
            if (number is { } value)
            {
                foreach (var process in processes)
                {
                    logger?.LogTrace("Sending {Signal} to pid {Pid} ({Name})", SignalName(value), process.Pid, process.Name);
                    signal(process.Pid, value);
                }
            }

            if (processes.Count > 0)
            {
                logger?.LogDebug("Waiting up to {Grace} ms for {Count} game process(es) to leave: {Processes}",
                    (long)grace.TotalMilliseconds, processes.Count, string.Join(", ", processes.Select(p => $"{p.Name} ({p.Pid})")));
            }

            var deadline = Stopwatch.StartNew();
            while (processes.Count > 0 && deadline.Elapsed < grace)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                processes = FindStragglers();
            }
            return processes;
        }

        private IReadOnlyList<TrackedProcess> FindStragglers()
            => detector.Running(installation, protectedPids).Game;
    }

    /// <summary>The parent of <paramref name="pid"/> as <c>/proc/&lt;pid&gt;/stat</c> reports it, or null when it cannot be read.</summary>
    internal static int? ParentOf(int pid)
    {
        try
        {
            // "pid (comm) state ppid ...": comm may hold spaces and parentheses, so read after the last ')'.
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && int.TryParse(fields[1], out var parent) ? parent : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
