using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Knows which game installations this process is currently running a launch for, and starts launches that
/// the game is started for from the outside (a store client) instead of as our own child.
/// </summary>
public interface IGameLaunchSession
{
    /// <summary>
    /// True from the moment the launch lock for <paramref name="installation"/> is taken until it is released,
    /// whatever phase the launch is in (preparing, running, tearing down). Writers that change the game folder
    /// or the loadouts behind it refuse to run while this is true.
    /// </summary>
    bool IsRunning(GameInstallation installation);

    /// <summary>
    /// True while the launch holding <paramref name="installation"/> is past the game and taking its changes back.
    /// A refusal to write says so in its wording, because the game is no longer running and a retry in a moment works.
    /// </summary>
    bool IsFinishing(GameInstallation installation);

    /// <summary>
    /// Why a write to <paramref name="installation"/> must wait, or null when nothing has the game. Unlike
    /// <see cref="IsRunning"/> it also reads the launch lock other SilverModManager processes hold and the
    /// process table, so it is for the writers that refuse, once per user action, and not for anything that polls.
    /// </summary>
    RunningGame? Describe(GameInstallation installation);

    /// <summary>
    /// Prepares a launch of <paramref name="loadout"/>. Nothing happens until <see cref="IGameLaunch.RunAsync"/>.
    /// The caller disposes the launch once it is done with it, which releases the installation lock.
    /// </summary>
    /// <param name="loadout">The loadout to apply.</param>
    /// <param name="runSpec">
    /// What the loadout's run tool wants started in place of the game's own executable, or null for a launch of any
    /// other program. The session records it where a store client's launch wrapper can find it, because the client's
    /// start request cannot carry it (Steam asks the user to confirm arguments given in the URL).
    /// </param>
    IGameLaunch BeginSession(Loadout.ReadOnly loadout, LaunchSpec? runSpec = null);
}

/// <summary>
/// What has the game, as <see cref="IGameLaunchSession.Describe"/> found it. <see cref="Message"/> is written for the player.
/// </summary>
/// <param name="Message">The refusal, naming what has the game and what to do.</param>
public abstract record RunningGame(string Message);

/// <summary>A launch in this process holds the installation. Writers keep their own wording for it.</summary>
/// <param name="Message">The default wording.</param>
/// <param name="Phase">Where the launch is; a finishing launch needs a retry in a moment, not a quit.</param>
public sealed record RunningInThisProcess(string Message, LaunchPhase Phase) : RunningGame(Message);

/// <summary>Another SilverModManager process holds the installation's launch lock.</summary>
/// <param name="Message">Names the holder and its loadout when its state file could be read.</param>
/// <param name="Pid">The holder's process id, or null when its state file was missing or unreadable.</param>
public sealed record HeldByOtherProcess(string Message, int? Pid) : RunningGame(Message);

/// <summary>The game, or a tool the app started in its prefix, runs without any launch of ours holding the game.</summary>
/// <param name="Message">Names the first process and says to quit it.</param>
/// <param name="Processes">What the process table showed.</param>
public sealed record RunningOutsideApp(string Message, RunningProcesses Processes) : RunningGame(Message);

/// <summary>
/// One launch whose game is started by the caller. The session takes the installation lock, applies the loadout
/// and marks the launch as running before the caller starts anything, then ingests the game's changes once the
/// caller says the game is gone.
/// </summary>
public interface IGameLaunch : IDisposable
{
    /// <summary>
    /// Takes the lock, applies the loadout, runs <paramref name="whileRunning"/> with the launch marked as
    /// running, then shuts down what is left of the game and takes its changes back. The callback is where the
    /// caller starts the game and waits for it. Throws <see cref="GameLaunchException"/> when the launch was
    /// refused, and the failing step's own exception when one crashed, after the failure has gone to the log.
    /// </summary>
    Task RunAsync(Func<CancellationToken, Task> whileRunning, CancellationToken cancellationToken);

    /// <summary>
    /// True when a Steam wrapper has recorded itself in the launch's state file, meaning the game is on its way
    /// even though no game process has shown up yet. Read from disk on every call.
    /// </summary>
    bool WrapperActive { get; }

    /// <summary>
    /// Says which tools the app started are still open in the game's prefix. They keep the launch running after
    /// the game quits and are never signalled, so the status names them. Pass the tools of the latest scan; an
    /// empty list clears the status.
    /// </summary>
    void ReportOpenTools(IReadOnlyList<TrackedProcess> tools);
}

/// <summary>
/// A launch was refused or ended in a way the player can act on, such as another launch holding the game or Steam
/// updating it. The message is written for the player. A step that crashed is not reported with this type: its own
/// exception is thrown instead, so it is shown as a crash.
/// </summary>
public sealed class GameLaunchException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The game was not started";
}

/// <summary>
/// The game was started and then asked to stop, but a process of it survived SIGKILL. Kept apart from
/// <see cref="GameLaunchException"/> because the game did start; what the player must do is stop that process.
/// </summary>
public sealed class GameDidNotStopException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The game did not stop";
}

/// <summary>
/// The game ran and exited, but taking the loadout off afterwards failed: a gate's teardown or the ingest of the
/// game's changes. The loadout is still applied to the game folder, and the player has to apply or revert it from
/// the app before playing outside it. The step's own exception is the inner exception, for the log.
/// </summary>
public sealed class LoadoutStillAppliedException(string message, Exception? cause = null) : UserFacingException(message, cause)
{
    /// <inheritdoc />
    public override string Title => "The loadout is still applied";
}
