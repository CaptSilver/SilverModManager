using NexusMods.Abstractions.Games;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// A <see cref="LaunchSession"/> as <see cref="IGameLaunch"/> sees it. Unlike a <c>run</c> process, which holds
/// the lock until the database is closed, the app keeps its database open, so disposing releases right away.
/// </summary>
internal sealed class GameLaunch(LaunchSession session, LaunchContext context) : IGameLaunch
{
    public bool WrapperActive => session.WrapperActive;

    public async Task RunAsync(Func<CancellationToken, Task> whileRunning, CancellationToken cancellationToken)
    {
        var result = await session.RunExternalAsync(context, whileRunning, cancellationToken);
        if (result.Message is { } message) throw new GameLaunchException(message);
    }

    public void Dispose() => session.Release();
}
