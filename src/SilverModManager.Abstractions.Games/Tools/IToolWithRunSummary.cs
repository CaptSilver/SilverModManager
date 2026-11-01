namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>What a finished run produced: the program's exit code and how many files it handed to the loadout.</summary>
/// <param name="CapturedFiles">Files captured from the tool's declared output folder; zero for other tools.</param>
public sealed record ToolRunSummary(int ExitCode, int CapturedFiles);

/// <summary>
/// Implemented by a tool that can say how its last run ended, so the page and the command line can word the
/// outcome the same way.
/// </summary>
public interface IToolWithRunSummary
{
    /// <summary>The last run that finished, or null when none has.</summary>
    ToolRunSummary? LastRun { get; }
}
