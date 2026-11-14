using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch.Starters;

/// <summary>
/// What the starters of a store client that stays open (Lutris, Heroic) share: the client's own exit says nothing
/// about the game, so the wait is for the game's processes to show up and then to be gone.
/// </summary>
internal static class ClientLaunchWait
{
    /// <summary>
    /// Polls until the installation shows a running process, then until the processes have been gone for the
    /// settle period. The start clock runs from the first poll and restarts every time
    /// <paramref name="clientIsPreparing"/> says the client is still busy; it is asked on every poll, so a
    /// caller can track change between polls in it.
    /// </summary>
    public static async Task<GameStartOutcome> UntilStartedThenGone(
        GameStartRequest request, IGameLaunch launch, IRunningGameDetector detector, Func<bool> clientIsPreparing,
        ILogger logger, string client, CancellationToken token)
    {
        var timing = request.Timing;
        var clock = timing.Clock;
        var since = clock.GetTimestamp();
        try
        {
            while (!detector.Running(request.Installation).Any)
            {
                token.ThrowIfCancellationRequested();
                if (clientIsPreparing())
                    since = clock.GetTimestamp();
                else if (clock.GetElapsedTime(since) >= timing.StartTimeout)
                {
                    logger.LogWarning("{Client} did not start {Game} within `{Timeout:g}`", client, request.GameName, timing.StartTimeout);
                    return GameStartOutcome.TimedOut;
                }

                await Task.Delay(timing.PollInterval, clock, token);
            }

            logger.LogDebug("{Game} is running, waiting for it to exit", request.GameName);
            await detector.WaitUntilGone(request.Installation, new GoneWait(timing.Settle, Interval: timing.PollInterval, Progress: OpenToolsProgress.Of(launch)), token);
            return GameStartOutcome.Exited;
        }
        catch (OperationCanceledException)
        {
            return GameStartOutcome.Cancelled;
        }
    }

    /// <summary>The store client of the request, which the selector only hands to the starter of its own kind.</summary>
    public static StoreClient ClientOf(GameStartRequest request, StoreClientKind kind)
        => request.Installation.LocatorResult.StoreClient is { } client && client.Kind == kind
            ? client
            : throw new InvalidOperationException($"{request.GameName} has no {kind} store client");

    /// <summary>
    /// Which copy of a client to run: its Flatpak first when the install came from there and the Flatpak is
    /// still present, then the binary on PATH, then the Flatpak anyway.
    /// </summary>
    public static ClientBinary Choose(bool preferFlatpak, bool nativeFound, Func<bool> flatpakInstalled)
    {
        bool? flatpak = null;
        bool FlatpakInstalled() => flatpak ??= flatpakInstalled();

        if (preferFlatpak && FlatpakInstalled()) return ClientBinary.Flatpak;
        if (nativeFound) return ClientBinary.Native;
        return FlatpakInstalled() ? ClientBinary.Flatpak : ClientBinary.None;
    }
}

/// <summary>A copy of a store client the app can run.</summary>
internal enum ClientBinary
{
    /// <summary>Neither the binary nor the Flatpak is there.</summary>
    None,

    /// <summary>The binary on <c>PATH</c>.</summary>
    Native,

    /// <summary>Through <c>flatpak run</c>.</summary>
    Flatpak,
}
