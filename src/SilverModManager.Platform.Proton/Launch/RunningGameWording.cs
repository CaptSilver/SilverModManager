using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// What the user is told when a game, or a tool the app started, is open where a launch or a write needs it closed.
/// </summary>
internal static class RunningGameWording
{
    /// <summary>For a write that must not run under a live game: apply, delete, remove.</summary>
    public static string Outside(string game, RunningProcesses running)
        => $"{game} is running ({Game(running)}). Quit it before applying, deleting or removing.";

    /// <summary>For a write refused because a tool the app started is still open.</summary>
    public static string ToolsOpen(string game, RunningProcesses running)
        => $"{Tool(running)} is open in {game}'s prefix. Close it first.";

    /// <summary>For a launch refused for either reason.</summary>
    public static string ForLaunch(string game, RunningProcesses running)
        => running.Game.Count > 0
            ? $"{game} is running ({Game(running)}). Quit it, then press Play again."
            : $"{Tool(running)} is open in {game}'s prefix. Close it, then press Play again.";

    /// <summary>For a pending finish held back because Steam is updating the game; the finisher and the Health Check say it alike.</summary>
    public static string SteamUpdating(string game, string loadoutName)
        => $"Steam is updating {game}, so the changes from your last session are not in '{loadoutName}' yet. The app finishes this after the update, the next time it applies or launches.";

    // The first process names the game; the rest would only lengthen a line the user reads once.
    private static string Game(RunningProcesses running)
    {
        var first = running.Game[0];
        return $"{first.Name}, pid {first.Pid}{(running.Game.Count > 1 ? ", ..." : "")}";
    }

    private static string Tool(RunningProcesses running)
    {
        var first = running.Tools[0];
        return $"{first.Name} (pid {first.Pid})";
    }
}
