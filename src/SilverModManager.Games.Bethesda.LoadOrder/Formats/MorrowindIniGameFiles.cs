// Follows libloadorder src/ini.rs (the [Game Files] section of Morrowind.ini).
using System.Text.RegularExpressions;

namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// The <c>[Game Files]</c> section of Morrowind.ini: <c>GameFile0=Morrowind.esm</c>, <c>GameFile1=...</c>, one
/// line per active plugin. Everything outside that section's <c>GameFileN</c> lines is left as it is.
/// </summary>
public static partial class MorrowindIniGameFiles
{
    private const string SectionName = "Game Files";

    // Morrowind.ini is a Windows file, so a file the game wrote has CRLF endings.
    private const string DefaultLineEnding = "\r\n";

    [GeneratedRegex(@"^\s*GameFile(\d+)\s*=(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameFileLine();

    /// <summary>The plugin names of the section, in the order of their numbers. Blank values are skipped.</summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        var found = new List<(long Number, int Position, string Name)>();
        var inSection = false;
        var position = 0;
        foreach (var line in Lines(text))
        {
            if (SectionOf(line) is { } section)
            {
                inSection = section.Equals(SectionName, StringComparison.OrdinalIgnoreCase);
                if (!inSection && found.Count > 0) break;
                continue;
            }

            if (!inSection || GameFileLine().Match(Content(line)) is not { Success: true } match) continue;

            var name = match.Groups[2].Value.Trim();
            if (name.Length == 0) continue;
            // A number too large for a long cannot be a real slot; order it last instead of throwing.
            found.Add((long.TryParse(match.Groups[1].Value, out var number) ? number : long.MaxValue, position++, name));
        }

        return found.OrderBy(f => f.Number).ThenBy(f => f.Position).Select(f => f.Name).ToList();
    }

    /// <summary>
    /// Replaces the <c>GameFileN</c> lines of the section with one line per name, numbered from 0. The new lines
    /// take the place of the first old one; with none, they follow the section's last line. A file without the
    /// section gets it at the end.
    /// </summary>
    public static string Rewrite(string existing, IReadOnlyList<string> names)
    {
        var lineEnding = existing.Contains('\n') ? (existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") : DefaultLineEnding;
        var lines = Lines(existing).ToList();
        var newLines = names.Select((name, i) => $"GameFile{i}={name}{lineEnding}").ToList();

        var start = lines.FindIndex(l => SectionOf(l) is { } s && s.Equals(SectionName, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && !EndsWithNewline(lines[^1])) lines[^1] += lineEnding;
            lines.Add($"[{SectionName}]{lineEnding}");
            lines.AddRange(newLines);
            return string.Concat(lines);
        }

        var end = start + 1;
        while (end < lines.Count && SectionOf(lines[end]) is null) end++;

        var firstOld = -1;
        var kept = new List<string>();
        for (var i = start + 1; i < end; i++)
        {
            if (GameFileLine().IsMatch(Content(lines[i])))
            {
                if (firstOld < 0) firstOld = kept.Count;
                continue;
            }
            kept.Add(lines[i]);
        }

        int insertAt;
        if (firstOld >= 0)
        {
            insertAt = firstOld;
        }
        else
        {
            // Before the blank lines that separate this section from the next, so the spacing survives.
            insertAt = kept.Count;
            while (insertAt > 0 && Content(kept[insertAt - 1]).Trim().Length == 0) insertAt--;
        }

        // The last line of the file may lack its terminator; the lines after it need one to start on their own line.
        if (newLines.Count > 0 && insertAt > 0 && !EndsWithNewline(kept[insertAt - 1])) kept[insertAt - 1] += lineEnding;
        if (newLines.Count > 0 && insertAt == 0 && !EndsWithNewline(lines[start])) lines[start] += lineEnding;
        kept.InsertRange(insertAt, newLines);

        // A section header ending the file without a newline stays as it was when there is nothing to put under it.
        var result = new List<string>(lines.Take(start + 1));
        result.AddRange(kept);
        result.AddRange(lines.Skip(end));
        return string.Concat(result);
    }

    private static string? SectionOf(string line)
    {
        var text = Content(line).Trim();
        return text.Length > 1 && text[0] == '[' && text[^1] == ']' ? text[1..^1].Trim() : null;
    }

    private static string Content(string line) => line.TrimEnd('\r', '\n');

    private static bool EndsWithNewline(string line) => line.EndsWith('\n');

    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            yield return text[start..end];
            start = end;
        }
    }
}
