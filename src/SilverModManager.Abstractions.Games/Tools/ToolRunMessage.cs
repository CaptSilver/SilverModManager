using System.Globalization;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>The one line that says how a tool run ended, shared by the Tools page and the command line.</summary>
public static class ToolRunMessage
{
    /// <param name="summary">What the tool reported, if it reports.</param>
    /// <param name="outputGroup">The group the tool's output goes to, if it has one.</param>
    /// <param name="movedFromGameFolder">Files the tool manager moved out of Overrides into the group.</param>
    public static string Finished(string toolName, ToolRunSummary? summary, string? outputGroup, int movedFromGameFolder = 0)
    {
        var line = summary is { } s ? $"{toolName} finished (exit code {s.ExitCode})." : $"{toolName} finished.";
        if (outputGroup is null) return line;

        var captured = (summary?.CapturedFiles ?? 0) + movedFromGameFolder;
        var files = captured switch
        {
            0 => "no files",
            1 => "1 file",
            _ => $"{captured.ToString("N0", CultureInfo.InvariantCulture)} files",
        };
        return $"{line} Captured {files} into '{outputGroup}'.";
    }
}
