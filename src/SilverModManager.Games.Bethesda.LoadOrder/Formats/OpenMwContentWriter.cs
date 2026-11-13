namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// Rewrites the <c>content=</c> and <c>groundcover=</c> lines of an openmw.cfg that belong to files under Data Files
/// and leaves every other byte alone: comments, blank lines, <c>data=</c> and <c>fallback=</c> lines, the line
/// endings, and lines for files the app never deploys (<c>builtin.omwscripts</c>, plugins from the user's own
/// <c>data=</c> folders).
/// </summary>
/// <remarks>
/// OpenMW loads plugins in the order of their <c>content=</c> lines, so the owned lines are rendered in the order
/// the app gives, as one block after the last line the app does not own. The block moves rather than the unowned
/// lines: those stay exactly where the user put them. <c>groundcover=</c> lines follow the same rule on their own,
/// and a name is never in both keys.
/// </remarks>
public static class OpenMwContentWriter
{
    private const string ContentPrefix = "content=";
    private const string GroundcoverPrefix = "groundcover=";

    /// <summary>Rewrites the lines for a list with no groundcover: an owned <c>groundcover=</c> line is removed.</summary>
    public static string Rewrite(string existing, IReadOnlyList<string> ordered, IReadOnlySet<string> owned) =>
        Rewrite(existing, ordered, [], owned);

    /// <param name="existing">The current text of openmw.cfg, empty when there is none.</param>
    /// <param name="ordered">The plugin file names to load as content, in load order. A name here is owned too.</param>
    /// <param name="groundcover">The plugin file names to load as groundcover, in load order. A name here is owned too.</param>
    /// <param name="owned">Every plugin file name the app deploys, loaded or not. A line for one of these that is
    /// in neither list is removed; a line for any other name is not touched.</param>
    public static string Rewrite(string existing, IReadOnlyList<string> ordered, IReadOnlyList<string> groundcover, IReadOnlySet<string> owned)
    {
        var lineEnding = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var groundcoverNames = groundcover.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A name asked for under both keys is groundcover: the switch is the more specific request.
        var orderedNames = ordered.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !groundcoverNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        var ownedNames = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        ownedNames.UnionWith(orderedNames);
        ownedNames.UnionWith(groundcoverNames);

        // Each line keeps its own terminator, so nothing but the lines we drop or add can differ.
        var lines = SplitKeepingTerminators(existing).ToList();
        lines = ReplaceOwnedBlock(lines, ContentPrefix, orderedNames, ownedNames, lineEnding);
        lines = ReplaceOwnedBlock(lines, GroundcoverPrefix, groundcoverNames, ownedNames, lineEnding);
        return string.Concat(lines);
    }

    /// <summary>The plugin names in the <c>content=</c> and <c>groundcover=</c> lines of an openmw.cfg, in line order, each once.</summary>
    public static IReadOnlyList<string> ContentNames(string text) =>
        SplitKeepingTerminators(text).Select(line => KeyName(line, ContentPrefix) ?? KeyName(line, GroundcoverPrefix)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The plugin names of the <c>content=</c> lines only, in line order, each once.</summary>
    public static IReadOnlyList<string> PlainContentNames(string text) => KeyNames(text, ContentPrefix);

    /// <summary>The plugin names of the <c>groundcover=</c> lines, in line order, each once.</summary>
    public static IReadOnlyList<string> GroundcoverNames(string text) => KeyNames(text, GroundcoverPrefix);

    private static IReadOnlyList<string> KeyNames(string text, string prefix) =>
        SplitKeepingTerminators(text).Select(line => KeyName(line, prefix)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> ReplaceOwnedBlock(List<string> source, string prefix, List<string> orderedNames, HashSet<string> ownedNames, string lineEnding)
    {
        var lines = new List<string>(source.Count);
        var lastUnowned = -1;
        foreach (var line in source)
        {
            var name = KeyName(line, prefix);
            if (name is not null)
            {
                if (ownedNames.Contains(name)) continue;
                lastUnowned = lines.Count;
            }
            lines.Add(line);
        }

        // After the last unowned line of this key, or at the end of the file when there is none.
        var insertAt = lastUnowned >= 0 ? lastUnowned + 1 : lines.Count;
        var block = orderedNames.Select(name => prefix + name + lineEnding).ToList();
        if (block.Count > 0 && insertAt > 0 && !EndsWithNewline(lines[insertAt - 1]))
            lines[insertAt - 1] += lineEnding;
        lines.InsertRange(insertAt, block);
        return lines;
    }

    private static string? KeyName(string line, string prefix)
    {
        var text = line.AsSpan().TrimEnd("\r\n").TrimStart();
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return text[prefix.Length..].Trim().ToString();
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
