using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Refuses a launch while the game, or a tool the app started in its prefix, is already running. Without it a launch
/// over a game the user started by hand applies the loadout under the live game: the installation lock is free and
/// the session's own registration is ours.
/// </summary>
/// <remarks>
/// <see cref="LaunchSession"/> asks this gate before every other one, so a refusal never lets a gate that records
/// something see a launch that is not going to happen.
/// </remarks>
public sealed class RunningGameGate(IRunningGameDetector detector) : ILaunchGate
{
    /// <inheritdoc />
    public Veto? Check(LaunchContext context)
    {
        var running = detector.Running(context.Installation, context.ProtectedPids);
        return running.Any ? new Veto(RunningGameWording.ForLaunch(context.Installation.Game.DisplayName, running)) : null;
    }

    /// <inheritdoc />
    public Task Prepare(LaunchContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Finish(LaunchContext context) => Task.CompletedTask;
}
