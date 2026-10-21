namespace NexusMods.Games.CreationEngine.Morrowind.OpenMw;

/// <summary>
/// Rewrites the <c>content=</c> lines of an openmw.cfg that belong to files under Data Files and leaves every
/// other byte alone: comments, blank lines, <c>data=</c> and <c>fallback=</c> lines, the line endings, and
/// <c>content=</c> lines for files the app never deploys (<c>builtin.omwscripts</c>, plugins from the user's own
/// <c>data=</c> folders).
/// </summary>
/// <remarks>
/// Existing entries keep their order, because OpenMW's launcher puts masters before their dependants and the
/// user may have sorted by hand. New entries go in a block after the last <c>content=</c> line, masters and
/// game files first. Ordering new entries by their masters needs a reader for the Morrowind plugin header,
/// which does not exist yet.
/// </remarks>
public static class OpenMwContentWriter
{
    private const string ContentPrefix = "content=";

    /// <param name="existing">The current text of openmw.cfg, empty when there is none.</param>
    /// <param name="deployed">Plugin file names the loadout provides under Data Files right now, game files included.</param>
    /// <param name="owned">Every plugin file name that resolves under Data Files, deployed or not. Entries named here
    /// but missing from <paramref name="deployed"/> are removed; entries named in neither are not touched.</param>
    public static string Rewrite(string existing, IReadOnlySet<string> deployed, IReadOnlySet<string> owned)
    {
        var lineEnding = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var deployedNames = new HashSet<string>(deployed, StringComparer.OrdinalIgnoreCase);
        var ownedNames = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);

        // Each line keeps its own terminator, so nothing but the lines we drop or add can differ.
        var lines = new List<string>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastContent = -1;
        foreach (var line in SplitKeepingTerminators(existing))
        {
            var name = ContentName(line);
            if (name is not null)
            {
                listed.Add(name);
                if (ownedNames.Contains(name) && !deployedNames.Contains(name)) continue;
                lastContent = lines.Count;
            }
            lines.Add(line);
        }

        var missing = deployedNames
            .Where(name => !listed.Contains(name))
            .OrderBy(LoadGroup)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => ContentPrefix + name + lineEnding)
            .ToList();

        // Insert after the last content line, or at the end of the file when there is none.
        var insertAt = lastContent >= 0 ? lastContent + 1 : lines.Count;
        if (missing.Count > 0 && insertAt > 0 && !EndsWithNewline(lines[insertAt - 1]))
            lines[insertAt - 1] += lineEnding;
        lines.InsertRange(insertAt, missing);

        return string.Concat(lines);
    }

    /// <summary>The plugin names in the <c>content=</c> lines of an openmw.cfg.</summary>
    public static IReadOnlySet<string> ContentNames(string text) =>
        SplitKeepingTerminators(text).Select(ContentName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int LoadGroup(string name)
    {
        var extension = Path.GetExtension(name);
        if (extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".omwgame", StringComparison.OrdinalIgnoreCase)) return 0;
        if (extension.Equals(".omwscripts", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

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
