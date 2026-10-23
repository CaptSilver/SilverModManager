using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// A running process, as seen by <see cref="IProcessTracker"/>.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="Name">The kernel's name for the process (<c>comm</c>), at most 15 characters.</param>
[PublicAPI]
public sealed record TrackedProcess(int Pid, string Name);

/// <summary>
/// Answers whether a game, or the launcher around it, is still running, by looking at the process table
/// instead of at the process that was started. A game started through Steam or Heroic is not a child of
/// this app, and a Wine game shows up as a <c>wine</c> binary rather than its own executable.
/// </summary>
[PublicAPI]
public interface IProcessTracker
{
    /// <summary>
    /// The processes that belong to the installation: those running an executable inside the game folder,
    /// those started with the installation's Wine prefix in their environment, and Wine processes given a
    /// <c>Z:</c> path inside the game folder. A working directory inside the game folder is not enough on
    /// its own.
    /// </summary>
    IReadOnlyList<TrackedProcess> FindGameProcesses(GameInstallation installation);

    /// <summary>
    /// The processes whose name or executable file name is <paramref name="name"/>, ignoring case. Wine
    /// processes are found by the executable they run even though the kernel truncates their name.
    /// </summary>
    IReadOnlyList<TrackedProcess> FindByName(string name);

    /// <summary>
    /// Whether a Steam client is running.
    /// </summary>
    bool IsSteamRunning();

    /// <summary>
    /// The reaper process Steam started for the app, or null. Another game's reaper is not returned.
    /// </summary>
    TrackedProcess? FindReaperForApp(uint appId);
}
