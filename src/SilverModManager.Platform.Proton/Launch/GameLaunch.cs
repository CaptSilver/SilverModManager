using System.Runtime.ExceptionServices;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Platform.Proton.Launch;

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
        if (result.Message is not { } message) return;

        // A refusal the step raised keeps its type and action. A crash keeps its own exception, so it is
        // reported as a crash and not as a calm "not started".
        if (result.Cause is { } cause && (cause is UserFacingException || !result.Refused))
            ExceptionDispatchInfo.Capture(cause).Throw();

        throw new GameLaunchException(message);
    }

    public void Dispose() => session.Release();
}
