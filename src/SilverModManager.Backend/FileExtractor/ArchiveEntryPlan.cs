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
    /// <param name="SymlinkTarget">The target 7z lists for a symbolic link, when the format stores one in the listing.</param>
    /// <param name="HardLinkTarget">The archive path a hard link points at.</param>
    /// <param name="IsSymlink">True for a symbolic link, whether or not the listing shows its target.</param>
    internal readonly record struct Entry(string Name, bool IsDirectory, string? SymlinkTarget = null, string? HardLinkTarget = null, bool IsSymlink = false);

    /// <summary>Raw names the bulk extraction has to leave out.</summary>
    internal IReadOnlySet<string> Excluded { get; }

    /// <summary>Files to extract one at a time, as raw name and cleaned path.</summary>
    internal IReadOnlyList<(string Name, string Path)> Moved { get; }

    /// <summary>Cleaned paths of rewritten folder entries, created directly.</summary>
    internal IReadOnlyList<string> Folders { get; }

    /// <summary>
    /// Symbolic link entries, as raw name, cleaned path and the target when the listing gave one. None reach
    /// the bulk run: the extractor decides each from its target and either copies the file or drops it.
    /// </summary>
    internal IReadOnlyList<(string Name, string Path, string? Target)> Links { get; }

    /// <summary>Hard links left out of the bulk run, as raw name and why.</summary>
    internal IReadOnlyList<(string Name, string Reason)> DroppedHardLinks { get; }

    internal ArchiveEntryPath.Rewrites Rewrites { get; }

    /// <summary>True when 7z's own extraction already gives the right result for every entry.</summary>
    internal bool IsPlain => Excluded.Count == 0 && Folders.Count == 0 && Moved.Count == 0 && Links.Count == 0;

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
            // Links never supersede a file: the file at a link's path wins (see ExtractedLinks.Place).
            if (!entries[i].IsDirectory && !IsLink(entries[i]) && cleaned[i].Length > 0) lastForPath[cleaned[i]] = i;
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var moved = new List<(string, string)>();
        var folders = new List<string>();
        var movedNames = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<(string, string, string?)>();
        var linkPrefixes = entries.Where(e => e.IsSymlink).Select(e => e.Name.TrimEnd('/') + "/").ToArray();
        var droppedHardLinks = new List<(string, string)>();

        for (var i = 0; i < entries.Count; i++)
        {
            var (name, isDirectory) = (entries[i].Name, entries[i].IsDirectory);
            var rewritten = ArchiveEntryPath.IsRewritten(name, cleaned[i]);
            if (rewritten) Rewrites.Add(name, cleaned[i]);

            // Excluding a name also excludes everything below it, so an entry that goes through a link
            // entry can't stay in the bulk run: it is extracted alone, which makes 7z create a real folder.
            var underLink = linkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
            var placedRight = PlacedBy7z(name) == cleaned[i] && !underLink;

            if (cleaned[i].Length == 0)
            {
                // A folder entry for the root itself ("." or "./") has nothing to create.
                if (!(isDirectory && placedRight)) excluded.Add(name);
                continue;
            }

            if (entries[i].IsSymlink)
            {
                excluded.Add(name);
                links.Add((name, cleaned[i], entries[i].SymlinkTarget));
                continue;
            }

            if (entries[i].HardLinkTarget is { } hardTarget)
            {
                // 7z creates a hard link next to the files it extracted, so one whose target is a kept
                // file cannot reach outside. Any other target gives 7z nothing to link and fails the run.
                if (placedRight && kept.Contains(hardTarget))
                {
                    kept.Add(name);
                }
                else
                {
                    excluded.Add(name);
                    droppedHardLinks.Add((name, $"its target `{hardTarget}` is not an earlier file extracted in place"));
                }

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
        Links = links;
        DroppedHardLinks = droppedHardLinks;
    }

    private static bool IsLink(Entry entry) => entry.IsSymlink || entry.HardLinkTarget is not null;
}
