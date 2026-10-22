namespace NexusMods.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// Rewrites the <c>content=</c> lines of an openmw.cfg that belong to files under Data Files and leaves every
/// other byte alone: comments, blank lines, <c>data=</c> and <c>fallback=</c> lines, the line endings, and
/// <c>content=</c> lines for files the app never deploys (<c>builtin.omwscripts</c>, plugins from the user's own
/// <c>data=</c> folders).
/// </summary>
/// <remarks>
/// OpenMW loads plugins in the order of their <c>content=</c> lines, so the owned lines are rendered in the order
/// the app gives, as one block after the last line the app does not own. The block moves rather than the unowned
/// lines: those stay exactly where the user put them.
/// </remarks>
public static class OpenMwContentWriter
{
    private const string ContentPrefix = "content=";

    /// <param name="existing">The current text of openmw.cfg, empty when there is none.</param>
    /// <param name="ordered">The plugin file names to load from Data Files, in load order. A name here is owned too.</param>
    /// <param name="owned">Every plugin file name that resolves under Data Files, loaded or not. A line for one of
    /// these that is not in <paramref name="ordered"/> is removed; a line for any other name is not touched.</param>
    public static string Rewrite(string existing, IReadOnlyList<string> ordered, IReadOnlySet<string> owned)
    {
        var lineEnding = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var orderedNames = ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ownedNames = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        ownedNames.UnionWith(orderedNames);

        // Each line keeps its own terminator, so nothing but the lines we drop or add can differ.
        var lines = new List<string>();
        var lastUnownedContent = -1;
        foreach (var line in SplitKeepingTerminators(existing))
        {
            var name = ContentName(line);
            if (name is not null)
            {
                if (ownedNames.Contains(name)) continue;
                lastUnownedContent = lines.Count;
            }
            lines.Add(line);
        }

        // After the last unowned content line, or at the end of the file when there is none.
        var insertAt = lastUnownedContent >= 0 ? lastUnownedContent + 1 : lines.Count;
        var block = orderedNames.Select(name => ContentPrefix + name + lineEnding).ToList();
        if (block.Count > 0 && insertAt > 0 && !EndsWithNewline(lines[insertAt - 1]))
            lines[insertAt - 1] += lineEnding;
        lines.InsertRange(insertAt, block);

        return string.Concat(lines);
    }

    /// <summary>The plugin names in the <c>content=</c> lines of an openmw.cfg, in line order, each once.</summary>
    public static IReadOnlyList<string> ContentNames(string text) =>
        SplitKeepingTerminators(text).Select(ContentName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? ContentName(string line)
    {
        var text = line.AsSpan().TrimEnd("\r\n").TrimStart();
        if (!text.StartsWith(ContentPrefix, StringComparison.Ordinal)) return null;
        return text[ContentPrefix.Length..].Trim().ToString();
    }

    private static bool EndsWithNewline(string line) => line.EndsWith('\n');

    private static IEnumerable<string> SplitKeepingTerminators(string text)
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
