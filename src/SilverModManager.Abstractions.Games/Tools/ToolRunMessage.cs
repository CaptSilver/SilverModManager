using System.Globalization;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>The one line that says how a tool run ended, shared by the Tools page and the command line.</summary>
public static class ToolRunMessage
{
    /// <param name="exitCode">The program's exit code, when the tool reports one.</param>
    /// <param name="outputGroup">The group the tool's output goes to, if it has one.</param>
    /// <param name="capturedFiles">How many files went into that group.</param>
    public static string Finished(string toolName, int? exitCode, string? outputGroup, int capturedFiles = 0)
    {
        var line = exitCode is { } code ? $"{toolName} finished (exit code {code})." : $"{toolName} finished.";
        if (outputGroup is null) return line;

        var files = capturedFiles switch
        {
            0 => "no files",
            1 => "1 file",
            _ => $"{capturedFiles.ToString("N0", CultureInfo.InvariantCulture)} files",
        };
        return $"{line} Captured {files} into '{outputGroup}'.";
    }

    /// <summary>The progress line shown while a tool runs: "Running DynDOLOD for 12 min".</summary>
    public static string Running(string toolName, TimeSpan elapsed)
    {
        var minutes = (int)elapsed.TotalMinutes;
        return minutes < 1 ? $"Running {toolName} for less than a minute" : $"Running {toolName} for {minutes} min";
    }
}
