using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>Writes the message a player reads when a tool exits with a failing code.</summary>
public static class ToolExitMessage
{
    /// <summary>
    /// Markdown: the one-line summary, the hint when there is one, and the output excerpt when the run was short
    /// or something matched. A long run without a hint points at the process log instead, since the tail of a
    /// 40 minute run says little about why it stopped.
    /// </summary>
    public static string For(string toolName, ToolRunResult result, string? hint, AbsolutePath? keptFolder = null, TimeSpan? startFailureWindow = null)
    {
        var window = startFailureWindow ?? ModdingTool.StartFailureWindow;
        var summary = $"{MarkdownText.Inline(toolName)} exited with code {result.ExitCode} after {Duration(result.Duration)}, run through {MarkdownText.InlineMidLine(result.Strategy)}";
        if (result.FallbackReason is { } reason) summary += $" because {MarkdownText.InlineMidLine(reason)}";
        summary += ".";

        var excerpt = result.Duration <= window || hint is not null
            ? WineOutputHints.Significant(result.StderrTail, result.StdoutTail)
            : [];
        var showExcerpt = excerpt.Count > 0;
        if (!showExcerpt) summary += " See the process log.";
        if (keptFolder is { } folder) summary += $" Its output was kept at {MarkdownText.Code(folder)}.";

        var lines = new List<string> { summary };
        if (hint is not null) lines.Add(hint);
        if (showExcerpt) lines.Add($"\nWhat Wine reported:\n\n{MarkdownText.CodeBlock(string.Join('\n', excerpt))}");
        return string.Join('\n', lines);
    }

    /// <summary>"4 s" under a minute, else whole minutes; exactly 60 s reads "1 min".</summary>
    public static string Duration(TimeSpan duration)
        => duration < TimeSpan.FromMinutes(1) ? $"{(int)duration.TotalSeconds} s" : $"{(int)duration.TotalMinutes} min";
}
