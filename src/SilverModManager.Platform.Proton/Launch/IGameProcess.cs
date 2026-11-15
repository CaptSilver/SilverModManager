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
    Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default, IProgress<RunningProcesses>? progress = null);

    /// <inheritdoc cref="GameProcess.Terminate"/>
    void Terminate();
}

/// <summary>
/// Waits that keep the game under the caller's control.
/// </summary>
public static class GameProcessWaits
{
    /// <summary>
    /// Waits for the game. On any exception, a cancelled wait included, terminates it before rethrowing: a game never
    /// outlives a wait that failed.
    /// </summary>
    public static async Task<GameProcessResult> WaitOrTerminateAsync(this IGameProcess process, CancellationToken cancellationToken, IProgress<RunningProcesses>? progress = null)
    {
        try
        {
            return await process.WaitAsync(cancellationToken, progress);
        }
        catch
        {
            process.Terminate();
            throw;
        }
    }
}

/// <summary>
/// Starts game commands. Exists so a launch can be run against a fake process.
/// </summary>
public interface IGameProcessFactory
{
    /// <summary>
    /// Starts <paramref name="spec"/> for <paramref name="installation"/>, which is what leftover processes are
    /// attributed to after the child exits. <paramref name="protectedPids"/> are never counted or signalled, with
    /// their ancestors.
    /// </summary>
    IGameProcess Start(GameProcessSpec spec, GameInstallation installation, IReadOnlySet<int>? protectedPids = null);

    /// <summary>
    /// Stops what is left of <paramref name="installation"/>'s game when it was started by someone else, such as
    /// Steam: waits for the processes to leave, then sends SIGTERM, then SIGKILL. Returns a process that survived
    /// SIGKILL, or null when the game is gone.
    /// </summary>
    Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, IReadOnlySet<int>? protectedPids, CancellationToken cancellationToken);
}

internal sealed class GameProcessFactory(IRunningGameDetector detector, StopHandler? stop = null, bool leaveSignalsAlone = false, ILogger<GameProcessFactory>? logger = null, IGameRegistry? registry = null) : IGameProcessFactory
{
    // A stop handler already terminates the game on SIGTERM and SIGHUP, in the phase-aware way.
    internal bool ForwardTermination => stop is null;

    public IGameProcess Start(GameProcessSpec spec, GameInstallation installation, IReadOnlySet<int>? protectedPids = null)
        => GameProcess.Start(spec, detector, installation, forwardTermination: ForwardTermination, leaveSignalsAlone: leaveSignalsAlone, logger: logger, isInstalledSteamAppId: registry is { } known ? appId => IsInstalledSteamApp(known, appId) : null, protectedPids: protectedPids);

    private static bool IsInstalledSteamApp(IGameRegistry registry, string appId) => registry.LocateGameInstallations()
        .Any(i => i.LocatorResult.Store == GameStore.Steam && i.LocatorResult.StoreIdentifier == appId);

    public Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, IReadOnlySet<int>? protectedPids, CancellationToken cancellationToken)
        => StragglerShutdown.RunAsync(detector, installation, GameProcess.DefaultGrace, cancellationToken, protectedPids, logger: logger);
}
