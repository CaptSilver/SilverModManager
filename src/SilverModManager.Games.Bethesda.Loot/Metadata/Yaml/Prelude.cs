namespace NexusMods.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>A masterlist whose <c>prelude:</c> block has been replaced, and where the new block sits.</summary>
/// <param name="Masterlist">The text to parse.</param>
/// <param name="Diff">Null if the masterlist has no <c>prelude:</c> key and so was not changed.</param>
internal readonly record struct ReplacedPrelude(string Masterlist, PreludeDiffSpan? Diff);

/// <summary>
/// Swaps a masterlist's <c>prelude:</c> block for the contents of a separate prelude file, so the
/// masterlist's aliases resolve against the current shared definitions. Follows libloot's
/// metadata/metadata_document.rs (<c>replace_prelude</c>).
/// </summary>
internal static class Prelude
{
    private const string PreludeKey = "prelude:";

    /// <summary>
    /// Replaces the body of the masterlist's top-level <c>prelude:</c> key with <paramref name="prelude"/>,
    /// indented to sit under it. The body runs until the next line that starts with something other
    /// than a space, <c>#</c>, CR or LF.
    /// </summary>
    public static ReplacedPrelude Replace(string masterlist, string prelude)
    {
        var split = SplitOnPrelude(masterlist);
        if (split is not var (beforePrelude, oldPrelude, afterPrelude)) return new ReplacedPrelude(masterlist, null);

        var indented = Indent(prelude);

        var replaced = afterPrelude.Length == 0
            ? $"{beforePrelude}\n{indented}"
            : $"{beforePrelude}\n{indented}\n{afterPrelude}";

        // The old prelude included leading and trailing newlines but the new prelude doesn't.
        var countModifier = afterPrelude.Length == 0 ? 1 : 2;
        var oldLineCount = Math.Max(0, CountLines(oldPrelude) - countModifier);

        // Plus one because beforePrelude does not include the line break after the prelude: key.
        var startLine = CountLines(beforePrelude) + 1;
        var newLineCount = CountLines(indented);
        var endLine = startLine + Math.Max(0, newLineCount - 1);

        return new ReplacedPrelude(replaced, new PreludeDiffSpan(startLine, endLine, newLineCount - oldLineCount));
    }

    /// <summary>
    /// <c>beforePrelude</c> ends with the <c>prelude:</c> key; <c>prelude</c> is the key's body,
    /// leading line break and trailing one included; <c>afterPrelude</c> is the rest.
    /// </summary>
    private static (string BeforePrelude, string Prelude, string AfterPrelude)? SplitOnPrelude(string masterlist)
    {
        int keyEnd;
        if (masterlist.StartsWith(PreludeKey, StringComparison.Ordinal))
        {
            keyEnd = PreludeKey.Length;
        }
        else
        {
            var position = masterlist.IndexOf("\n" + PreludeKey, StringComparison.Ordinal);
            if (position < 0) return null;

            keyEnd = position + 1 + PreludeKey.Length;
        }

        var before = masterlist[..keyEnd];
        var remainder = masterlist[keyEnd..];

        for (var i = 0; i < remainder.Length - 1; i++)
        {
            if (remainder[i] != '\n') continue;
            if (remainder[i + 1] is ' ' or '#' or '\n' or '\r') continue;

            return (before, remainder[..(i + 1)], remainder[(i + 1)..]);
        }

        return (before, remainder, "");
    }

    /// <summary>
    /// Rust's <c>lines().count()</c> plus one if the text ends in a line break: one more than the
    /// number of line breaks, and none for an empty text.
    /// </summary>
    private static int CountLines(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

    private static string Indent(string prelude)
    {
        var indented = ("  " + prelude.Replace("\n", "\n  "))
            .Replace("  \r\n", "\r\n")
            .Replace("  \n", "\n");

        return indented.EndsWith("\n  ", StringComparison.Ordinal) ? indented.TrimEnd(' ') : indented;
    }
}
