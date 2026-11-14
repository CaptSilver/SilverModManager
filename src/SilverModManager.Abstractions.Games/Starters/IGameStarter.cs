using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Starters;

/// <summary>Which way a game is started, one per kind of client or program.</summary>
public enum GameStarterKind
{
    /// <summary>Through <c>steam://run</c>, with the app's Steam launch option applying the spec.</summary>
    Steam,

    /// <summary>Through Heroic.</summary>
    Heroic,

    /// <summary>Through Lutris.</summary>
    Lutris,

    /// <summary>The program itself, through the tool runner, the shell or the process runner.</summary>
    Direct,

    /// <summary>A command the loadout replaced whole, started as a process of this app.</summary>
    Process,
}

/// <summary>How a started game's wait ended.</summary>
public enum GameStartOutcome
{
    /// <summary>The game started and is over.</summary>
    Exited,

    /// <summary>The client did not start the game in time.</summary>
    TimedOut,

    /// <summary>The caller stopped waiting.</summary>
    Cancelled,
}

/// <summary>
/// One way of starting a game. A starter is the only store-specific code on the launch path: the tool picks one,
/// asks it whether the launch can go ahead, and runs it inside the launch session.
/// </summary>
public interface IGameStarter
{
    /// <summary>The kind the selector matches against a store client.</summary>
    GameStarterKind Kind { get; }

    /// <summary>
    /// Whether the client can hand <see cref="GameStartRequest.AllArgs"/> to the game. Starters that run the
    /// program themselves can always.
    /// </summary>
    bool CanCarryArguments(GameStartRequest request);

    /// <summary>Throws <see cref="GameLaunchException"/> before any session exists, so nothing is applied for a launch that cannot run.</summary>
    void CheckCanStart(GameStartRequest request);

    /// <summary>Starts the game and waits until it is over. Runs inside the launch session.</summary>
    Task<GameStartOutcome> Start(GameStartRequest request, IGameLaunch launch, CancellationToken token);

    /// <summary>What to tell the user once the session has finished, or null.</summary>
    string? AfterSessionMessage(GameStartRequest request, GameStartOutcome outcome);
}

/// <summary>The starter picked for one launch, and the notice to show when it is not the client's own.</summary>
public sealed record StarterChoice(IGameStarter Starter, string? FallbackNotice);

/// <summary>Picks the starter for a launch.</summary>
public interface IGameStarterSelector
{
    /// <summary>
    /// The starter for <paramref name="request"/>. The Steam starter belongs to the tool, since it carries the
    /// tool's Steam wait state, so it comes in as an argument; the others are shared.
    /// </summary>
    StarterChoice For(GameStartRequest request, IGameStarter steam);
}
