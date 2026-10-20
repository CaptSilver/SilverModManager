using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Launch;

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
}

internal sealed class GameProcessFactory(IProcessTracker tracker) : IGameProcessFactory
{
    public IGameProcess Start(GameProcessSpec spec, GameInstallation installation)
        => GameProcess.Start(spec, tracker, installation);
}
