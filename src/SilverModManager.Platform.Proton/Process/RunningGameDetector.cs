using Microsoft.Extensions.Logging;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton;

/// <summary>
/// Answers "is the game still running" for every wait and sweep: the tracker's processes for the installation, minus
/// this app's own family, its ancestors and the processes the caller protects, with the tools the app started set apart.
/// </summary>
/// <remarks>
/// Our ancestors have to go. A headless <c>run</c> under <c>gamescope -- silvermodmanager run %command%</c> has parents
/// that carry the game's prefix variable, and counting one of them as the game would refuse the launch gate and keep
/// the wait from ever ending.
/// </remarks>
public sealed class RunningGameDetector : IRunningGameDetector
{
    /// <summary>The time between two scans of the process table.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(300);

    private readonly IProcessTracker _tracker;
    private readonly IStartedTools _tools;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunningGameDetector>? _logger;
    private readonly Func<int, int?> _parentOf;
    private readonly OwnProcessFamily _family;
    private readonly int _ownPid;
    private readonly TimeSpan _interval;

    public RunningGameDetector(IProcessTracker tracker, IStartedTools tools, TimeProvider clock, ILogger<RunningGameDetector>? logger = null)
        : this(tracker, tools, clock, logger, tracker.ParentOf, OwnProcessFamily.Current, Environment.ProcessId, DefaultInterval)
    {
    }

    internal RunningGameDetector(
        IProcessTracker tracker, IStartedTools tools, TimeProvider clock, ILogger<RunningGameDetector>? logger,
        Func<int, int?> parentOf, OwnProcessFamily family, int ownPid, TimeSpan interval)
    {
        _tracker = tracker;
        _tools = tools;
        _clock = clock;
        _logger = logger;
        _parentOf = parentOf;
        _family = family;
        _ownPid = ownPid;
        _interval = interval;
    }

    /// <inheritdoc />
    public RunningProcesses Running(GameInstallation installation, IReadOnlySet<int>? protectedPids = null)
    {
        var excluded = AncestorsOf([_ownPid]);
        if (protectedPids is { Count: > 0 }) excluded.UnionWith(AncestorsOf(protectedPids));

        var game = new List<TrackedProcess>();
        var tools = new List<TrackedProcess>();
        foreach (var process in _family.Foreign(_tracker.FindGameProcesses(installation)))
        {
            if (excluded.Contains(process.Pid)) continue;
            if (StartedToolOf(process.Pid) is { } tool) tools.Add(process with { Name = tool });
            else game.Add(process);
        }
        return game.Count == 0 && tools.Count == 0 ? RunningProcesses.Empty : new RunningProcesses(game, tools);
    }

    /// <inheritdoc />
    public async Task<RunningProcesses> WaitUntilGone(GameInstallation installation, GoneWait wait, CancellationToken token)
    {
        var interval = wait.Interval ?? _interval;
        var lastSeen = _clock.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var running = Running(installation, wait.ProtectedPids);
            if (running.Any)
            {
                wait.Progress?.Report(running);
                lastSeen = _clock.GetTimestamp();
            }
            else if (_clock.GetElapsedTime(lastSeen) >= wait.Settle)
            {
                return RunningProcesses.Empty;
            }

            if (wait.Stop?.Requested == true)
            {
                _logger?.LogDebug("A stop was requested with {Game} game and {Tools} tool process(es) still running", running.Game.Count, running.Tools.Count);
                return running;
            }

            await Task.Delay(interval, _clock, token);
        }
    }

    // The pids and everything above them, up to but not including init.
    private HashSet<int> AncestorsOf(IEnumerable<int> starts)
    {
        var result = new HashSet<int>();
        foreach (var start in starts)
        {
            for (int? pid = start; pid is > 1 && result.Add(pid.Value); pid = _parentOf(pid.Value)) { }
        }
        return result;
    }

    // A tool's children (wine helpers, a launched editor) count as the tool, so each pid is checked up its own chain.
    private string? StartedToolOf(int pid)
    {
        var seen = new HashSet<int>();
        for (int? current = pid; current is > 1 && seen.Add(current.Value); current = _parentOf(current.Value))
        {
            if (_tools.NameOf(current.Value) is { } name) return name;
        }
        return null;
    }
}
