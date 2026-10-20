using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Games;

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
    IGameLaunch BeginSession(Loadout.ReadOnly loadout);
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
    /// refused or a step failed, after the failure has gone to the log and the desktop.
    /// </summary>
    Task RunAsync(Func<CancellationToken, Task> whileRunning, CancellationToken cancellationToken);

    /// <summary>
    /// True when a Steam wrapper has recorded itself in the launch's state file, meaning the game is on its way
    /// even though no game process has shown up yet. Read from disk on every call.
    /// </summary>
    bool WrapperActive { get; }
}

/// <summary>
/// A launch did not run to the end. The message is what the user was told.
/// </summary>
public sealed class GameLaunchException(string message) : Exception(message);
