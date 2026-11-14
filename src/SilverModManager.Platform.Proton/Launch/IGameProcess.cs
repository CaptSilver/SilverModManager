using Microsoft.Extensions.Logging;
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

internal sealed class GameProcessFactory(IRunningGameDetector detector, StopHandler? stop = null, bool leaveSignalsAlone = false, ILogger<GameProcessFactory>? logger = null, IGameRegistry? registry = null) : IGameProcessFactory
{
    // A stop handler already terminates the game on SIGTERM and SIGHUP, in the phase-aware way.
    internal bool ForwardTermination => stop is null;

    public IGameProcess Start(GameProcessSpec spec, GameInstallation installation)
        => GameProcess.Start(spec, detector, installation, forwardTermination: ForwardTermination, leaveSignalsAlone: leaveSignalsAlone, logger: logger, isInstalledSteamAppId: registry is { } known ? appId => IsInstalledSteamApp(known, appId) : null);

    private static bool IsInstalledSteamApp(IGameRegistry registry, string appId) => registry.LocateGameInstallations()
        .Any(i => i.LocatorResult.Store == GameStore.Steam && i.LocatorResult.StoreIdentifier == appId);

    public Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, CancellationToken cancellationToken)
        => StragglerShutdown.RunAsync(detector, installation, GameProcess.DefaultGrace, cancellationToken, logger: logger);
}
