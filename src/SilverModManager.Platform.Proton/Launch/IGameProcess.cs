using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// A started game command, as <see cref="LaunchSession"/> sees it.
/// </summary>
public interface IGameProcess : IDisposable
{
    /// <summary>The child's process id.</summary>
    int Pid { get; }

    /// <inheritdoc cref="GameProcess.WaitAsync"/>
    Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="GameProcess.Terminate"/>
    void Terminate();
}

/// <summary>
/// Starts game commands. Exists so a launch can be run against a fake process.
/// </summary>
public interface IGameProcessFactory
{
    /// <summary>
    /// Starts <paramref name="spec"/> for <paramref name="installation"/>, which is what leftover processes are
    /// attributed to after the child exits.
    /// </summary>
    IGameProcess Start(GameProcessSpec spec, GameInstallation installation);

    /// <summary>
    /// Stops what is left of <paramref name="installation"/>'s game when it was started by someone else, such as
    /// Steam: waits for the processes to leave, then sends SIGTERM, then SIGKILL. Returns a process that survived
    /// SIGKILL, or null when the game is gone.
    /// </summary>
    Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, CancellationToken cancellationToken);
}

internal sealed class GameProcessFactory(IProcessTracker tracker, StopHandler? stop = null, bool leaveSignalsAlone = false) : IGameProcessFactory
{
    // A stop handler already terminates the game on SIGTERM and SIGHUP, in the phase-aware way.
    internal bool ForwardTermination => stop is null;

    public IGameProcess Start(GameProcessSpec spec, GameInstallation installation)
        => GameProcess.Start(spec, tracker, installation, forwardTermination: ForwardTermination, leaveSignalsAlone: leaveSignalsAlone);

    public Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, CancellationToken cancellationToken)
        => StragglerShutdown.RunAsync(tracker, installation, GameProcess.DefaultGrace, cancellationToken);
}
