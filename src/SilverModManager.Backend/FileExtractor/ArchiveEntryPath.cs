using Microsoft.Extensions.Logging;

namespace NexusMods.Backend.FileExtractor;

/// <summary>
/// Maps the raw name of an archive entry to the path it gets inside the extraction folder. Archives in the
/// wild carry <c>..</c> segments, absolute paths, drive letters and Windows separators, and the author meant
/// the file to land where a well-formed archive would put it, so nothing is refused: the name is resolved
/// against the archive root and anything that would climb above it is dropped.
/// </summary>
internal static class ArchiveEntryPath
{
    /// <summary>
    /// Returns the cleaned path with <c>/</c> separators and no leading or trailing separator, or an empty
    /// string when nothing is left of the name.
    /// </summary>
    internal static string Clean(ReadOnlySpan<char> rawName)
    {
        var name = rawName;

        // A drive letter only counts at the very start: "a/C:/x" has a folder called "C:".
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':') name = name[2..];

        var segments = new List<string>();
        foreach (var segment in name.ToString().Split('/', '\\'))
        {
            if (segment.Length == 0 || segment.Contains('\0') || segment == ".") continue;
            if (segment == "..")
            {
                // At the root there is nothing to climb out of, so the segment is discarded.
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Whether <see cref="Clean"/> puts the entry somewhere its name doesn't plainly say. A trailing or
    /// doubled separator and a <c>.</c> segment don't count: <c>tar -C dir .</c> stores every entry as
    /// <c>./name</c>, and reporting each of those would bury the entries that really moved.
    /// </summary>
    internal static bool IsRewritten(string rawName, string cleaned)
    {
        // A leading separator makes the name absolute, which is worth reporting.
        if (rawName.StartsWith('/')) return true;
        return string.Join('/', rawName.Split('/').Where(segment => segment is not ("" or "."))) != cleaned;
    }

    /// <summary>
    /// Entries of one archive whose names changed or were dropped, kept so they are reported in a single
    /// warning and not one line per entry.
    /// </summary>
    internal sealed class Rewrites
    {
        private readonly List<string> _lines = [];

        internal bool IsEmpty => _lines.Count == 0;

        internal void Add(string original, string cleaned) => _lines.Add(cleaned.Length == 0 ? $"`{original}` -> (skipped)" : $"`{original}` -> `{cleaned}`");

        internal void AddOverwritten(string original, string cleaned) => _lines.Add($"`{original}` -> (skipped, `{cleaned}` is written again by a later entry)");

        internal void Log(ILogger logger, string archive)
        {
            if (IsEmpty) return;
            logger.LogWarning("Rewrote {Count} entry path(s) in archive `{Archive}` to stay inside the extraction folder: {Entries}", _lines.Count, archive, string.Join("; ", _lines));
        }
    }
}
