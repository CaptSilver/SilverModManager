namespace SilverModManager.Backend.FileExtractor;

/// <summary>
/// What to do with the entries of an archive that 7z lists, when 7z's own handling of their names differs
/// from <see cref="ArchiveEntryPath.Clean"/>. 7z drops every <c>..</c> segment instead of resolving it
/// against the previous one, and keeps <c>\</c> and <c>C:\</c> as part of a file name, so such entries are
/// extracted on their own and moved to the cleaned path. Every other entry, rewritten or not, is left to
/// the bulk run: each separate run decompresses the archive again.
/// </summary>
internal sealed class ArchiveEntryPlan
{
    internal readonly record struct Entry(string Name, bool IsDirectory);

    /// <summary>Raw names the bulk extraction has to leave out.</summary>
    internal IReadOnlySet<string> Excluded { get; }

    /// <summary>Files to extract one at a time, as raw name and cleaned path.</summary>
    internal IReadOnlyList<(string Name, string Path)> Moved { get; }

    /// <summary>Cleaned paths of rewritten folder entries, created directly.</summary>
    internal IReadOnlyList<string> Folders { get; }

    internal ArchiveEntryPath.Rewrites Rewrites { get; }

    /// <summary>True when 7z's own extraction already gives the right result for every entry.</summary>
    internal bool IsPlain => Excluded.Count == 0 && Folders.Count == 0 && Moved.Count == 0;

    /// <summary>
    /// Where 7z itself extracts an entry: it splits on <c>/</c> only and drops empty, <c>.</c> and
    /// <c>..</c> segments.
    /// </summary>
    internal static string PlacedBy7z(string rawName) =>
        string.Join('/', rawName.Split('/').Where(segment => segment is not ("" or "." or "..")));

    internal ArchiveEntryPlan(IReadOnlyList<Entry> entries)
    {
        Rewrites = new ArchiveEntryPath.Rewrites();
        var cleaned = entries.Select(e => ArchiveEntryPath.Clean(e.Name)).ToArray();

        // The last file written to a path wins, as it does for plain duplicates. Entries that merely repeat
        // a raw name are left to 7z, which overwrites in archive order.
        var lastForPath = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsDirectory && cleaned[i].Length > 0) lastForPath[cleaned[i]] = i;
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var moved = new List<(string, string)>();
        var folders = new List<string>();
        var movedNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            var (name, isDirectory) = entries[i];
            var rewritten = ArchiveEntryPath.IsRewritten(name, cleaned[i]);
            if (rewritten) Rewrites.Add(name, cleaned[i]);
            var placedRight = PlacedBy7z(name) == cleaned[i];

            if (cleaned[i].Length == 0)
            {
                // A folder entry for the root itself ("." or "./") has nothing to create.
                if (!(isDirectory && placedRight)) excluded.Add(name);
                continue;
            }

            if (isDirectory)
            {
                if (placedRight) continue;
                excluded.Add(name);
                folders.Add(cleaned[i]);
                continue;
            }

            var later = lastForPath[cleaned[i]];
            var superseded = later != i && entries[later].Name != name;
            if (superseded)
            {
                Rewrites.AddOverwritten(name, cleaned[i]);
                excluded.Add(name);
                continue;
            }

            if (placedRight)
            {
                kept.Add(name);
                continue;
            }

            excluded.Add(name);
            if (movedNames.Add(name)) moved.Add((name, cleaned[i]));
        }

        excluded.ExceptWith(kept);
        Excluded = excluded;
        Moved = moved;
        Folders = folders;
    }
}
