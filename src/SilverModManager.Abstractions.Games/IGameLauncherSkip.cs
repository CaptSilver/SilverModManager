using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Implemented by a game whose store start-up goes through a launcher program that takes arguments to skip itself.
/// </summary>
/// <remarks>
/// Steam's <c>%command%</c> is that launcher, so a launch option that runs it unsubstituted opens the launcher
/// window first. The launch options the app writes or tells the user to write carry these arguments after
/// <c>%command%</c>. The app's own launch runs the game's executable directly and never starts the launcher.
/// </remarks>
public interface IGameLauncherSkip
{
    /// <summary>
    /// The arguments that make the launcher start the game without showing itself.
    /// </summary>
    IReadOnlyList<string> LauncherSkipArguments { get; }
}

public static class GameLauncherSkip
{
    /// <summary>
    /// The launcher-skip arguments <paramref name="game"/> declares, or none for a game without a launcher.
    /// </summary>
    /// <remarks>
    /// Callers hold an <see cref="IGameData"/>; the registered game instance behind it is what implements
    /// <see cref="IGameLauncherSkip"/>.
    /// </remarks>
    public static IReadOnlyList<string> For(IGameData game)
    {
        return game is IGameLauncherSkip launcher ? launcher.LauncherSkipArguments : [];
    }
}
