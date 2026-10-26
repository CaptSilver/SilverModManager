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
    /// Prepares a launch of <paramref name="loadout"/>. Nothing happens until <see cref="IGameLaunch.RunAsync"/>.
    /// The caller disposes the launch once it is done with it, which releases the installation lock.
    /// </summary>
    /// <param name="loadout">The loadout to apply.</param>
    /// <param name="runSpec">
    /// What the loadout's run tool wants started, when the caller hands the game to a store client that starts
    /// it from its own command. The session records it where the client's launch wrapper can find it, because
    /// the client's start request cannot carry it (Steam asks the user to confirm arguments given in the URL).
    /// </param>
    IGameLaunch BeginSession(Loadout.ReadOnly loadout, LaunchSpec? runSpec = null);
}

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
