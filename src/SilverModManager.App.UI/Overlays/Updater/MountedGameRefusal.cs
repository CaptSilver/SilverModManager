using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.Overlays.Updater;

/// <summary>
/// A game is reading through a mount this process serves. Closing the window or replacing the app's own binary
/// ends the process, and the game would lose its folder in the middle of a read.
/// </summary>
public sealed class MountedGameException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "A game is still running";
}

/// <summary>
/// The sentence the window close and the self-update refuse with while a mount is held.
/// </summary>
public static class MountedGameRefusal
{
    /// <param name="held">The mounts this process holds.</param>
    /// <param name="verb">What was asked, as the start of a sentence: <c>Closing</c>, <c>Updating</c> or <c>Restarting</c>.</param>
    /// <returns>Null when nothing is held.</returns>
    public static string? Describe(HeldMounts held, string verb)
    {
        var games = held.Current.Select(static m => m.Installation.Game.DisplayName).Distinct().ToList();
        if (games.Count == 0) return null;

        var names = games.Count == 1 ? games[0] : $"{string.Join(", ", games.Take(games.Count - 1))} and {games[^1]}";
        var running = games.Count == 1 ? "is" : "are";
        return $"{names} {running} running from a mounted folder. {verb} SilverModManager now would crash {(games.Count == 1 ? "it" : "them")}. Quit {(games.Count == 1 ? "the game" : "the games")} first.";
    }
}
