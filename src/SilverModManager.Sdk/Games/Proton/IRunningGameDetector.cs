using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// What the process table shows for an installation, split into the game's own processes and the tools this app
/// started in its prefix (xEdit, LOOT and the like), which are never signalled but keep a session open.
/// </summary>
/// <param name="Game">Processes of the game, its launcher, Wine servers and Steam's reaper.</param>
/// <param name="Tools">Processes of a tool the app started, and their children; each carries the name the tool was started under, not the kernel's.</param>
[PublicAPI]
public sealed record RunningProcesses(IReadOnlyList<TrackedProcess> Game, IReadOnlyList<TrackedProcess> Tools)
{
    /// <summary>Nothing running.</summary>
    public static readonly RunningProcesses Empty = new([], []);

    /// <summary>Whether anything is running, game or tool.</summary>
    public bool Any => Game.Count > 0 || Tools.Count > 0;
}

/// <summary>
/// How long nothing may be seen running before a game counts as gone. A launcher can exit a few hundred
/// milliseconds before the game's process appears, and Steam replaces its reaper when it relaunches a game, so a
/// single empty scan proves nothing.
/// </summary>
/// <remarks>The values are unmeasured guesses, so they are constants here and not settings.</remarks>
[PublicAPI]
public static class SettlePeriods
{
    /// <summary>A game Steam starts: the client can take seconds to hand the game its reaper.</summary>
    public static readonly TimeSpan Steam = TimeSpan.FromSeconds(8);

    /// <summary>A game we start ourselves.</summary>
    public static readonly TimeSpan Direct = TimeSpan.FromSeconds(3);
}

/// <summary>Arguments of <see cref="IRunningGameDetector.WaitUntilGone"/>.</summary>
/// <param name="Settle">How long the scan has to stay empty.</param>
/// <param name="ProtectedPids">Processes to ignore, with their ancestors, such as a forwarding client waiting for the exit report.</param>
/// <param name="Stop">When set, the wait returns what is still running instead of waiting for it to leave.</param>
/// <param name="Interval">The poll interval; defaults to the detector's own.</param>
/// <param name="Progress">Receives every scan that found something.</param>
[PublicAPI]
public sealed record GoneWait(
    TimeSpan Settle,
    IReadOnlySet<int>? ProtectedPids = null,
    StopRequest? Stop = null,
    TimeSpan? Interval = null,
    IProgress<RunningProcesses>? Progress = null);

/// <summary>
/// Runs the handler on the reporting thread, in order. <see cref="Progress{T}"/> posts to the thread pool, which can
/// deliver two scans out of order, and a scan that names an open tool must not overtake the one that cleared it.
/// </summary>
/// <param name="handler">Called once per report.</param>
[PublicAPI]
public sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => handler(value);
}

/// <summary>
/// The one answer to "is this game running": the process tracker's list for the installation minus this app's own
/// process family, its ancestors and the processes the caller protects, with the tools the app started set apart.
/// </summary>
[PublicAPI]
public interface IRunningGameDetector
{
    /// <summary>One scan of the process table.</summary>
    RunningProcesses Running(GameInstallation installation, IReadOnlySet<int>? protectedPids = null);

    /// <summary>
    /// Polls until <see cref="Running"/> has been empty for <see cref="GoneWait.Settle"/> and returns
    /// <see cref="RunningProcesses.Empty"/>. Returns what is still running at once when <see cref="GoneWait.Stop"/> is set.
    /// </summary>
    Task<RunningProcesses> WaitUntilGone(GameInstallation installation, GoneWait wait, CancellationToken token);
}
