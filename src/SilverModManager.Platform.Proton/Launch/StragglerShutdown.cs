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
    /// <param name="excludedPids">
    /// Processes to leave alone, with all their ancestors. A forwarded launch's <c>run</c> process shares the game's
    /// prefix in its environment, so the tracker lists it, yet it is still waiting for the game's exit to be reported.
    /// </param>
    /// <param name="signal">Sends a signal to a pid and returns the call's result. Defaults to <c>kill(2)</c>.</param>
    /// <param name="parentOf">Gives a pid's parent, or null. Defaults to reading <c>/proc</c>.</param>
    public static async Task<TrackedProcess?> RunAsync(
        IProcessTracker tracker, GameInstallation installation, TimeSpan grace, CancellationToken cancellationToken,
        IReadOnlySet<int>? excludedPids = null, Func<int, int, int>? signal = null, Func<int, int?>? parentOf = null)
    {
        var sweep = new Sweep(tracker, installation, grace, signal ?? kill, ProtectedPids(excludedPids, parentOf ?? ParentOf));
        var remaining = await sweep.WaitUntilGoneAsync(number: null, cancellationToken);
        if (remaining.Count > 0) remaining = await sweep.WaitUntilGoneAsync(SigTerm, cancellationToken);
        if (remaining.Count > 0) remaining = await sweep.WaitUntilGoneAsync(SigKill, cancellationToken);
        return remaining.Count > 0 ? remaining[0] : null;
    }

    private sealed class Sweep(IProcessTracker tracker, GameInstallation installation, TimeSpan grace, Func<int, int, int> signal, HashSet<int> protectedPids)
    {
        public async Task<IReadOnlyList<TrackedProcess>> WaitUntilGoneAsync(int? number, CancellationToken cancellationToken)
        {
            var processes = FindStragglers();
            if (number is { } value)
            {
                foreach (var process in processes) signal(process.Pid, value);
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
            => tracker.FindGameProcesses(installation).Where(process => GameProcess.IsSafeToSignal(process.Pid) && !protectedPids.Contains(process.Pid)).ToList();
    }

    // The excluded processes and everything above them. Their parents (a shell, the AppImage runtime, Steam's
    // reaper) share the environment that makes the tracker list them, and end by themselves once the child does.
    private static HashSet<int> ProtectedPids(IReadOnlySet<int>? excluded, Func<int, int?> parentOf)
    {
        var result = new HashSet<int>();
        foreach (var start in excluded ?? new HashSet<int>())
        {
            for (int? pid = start; pid is > 1 && result.Add(pid.Value); pid = parentOf(pid.Value)) { }
        }
        return result;
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
