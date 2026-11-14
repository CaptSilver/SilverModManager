using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Starters;

public static class OpenToolsProgress
{
    /// <summary>
    /// A tool the app started in the prefix keeps the launch running after the game quits; the session names it.
    /// </summary>
    public static IProgress<RunningProcesses> Of(IGameLaunch launch)
        => new ImmediateProgress<RunningProcesses>(running => launch.ReportOpenTools(running.Tools));
}
