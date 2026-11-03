using System.Text;

namespace SilverModManager.Backend.FileExtractor;

/// <summary>
/// What to do with the entries of an archive that 7z lists, when 7z's own handling of their names differs
/// from <see cref="ArchiveEntryPath.Clean"/> and <see cref="EntryNames"/>. 7z drops every <c>..</c> segment
/// instead of resolving it against the previous one, and keeps <c>\</c> and <c>C:\</c> as part of a file
/// name, so such entries are extracted on their own and moved to the cleaned path. Every other entry,
/// rewritten or not, is left to the bulk run: each separate run decompresses the archive again. A name
/// 7z places in the right shape but with a trailing space or an undecoded byte stays in the bulk run
/// too, and <see cref="ExtractedNames"/> renames it afterwards. A link among those is removed there instead.
/// </summary>
internal sealed class ArchiveEntryPlan
{
    /// <param name="Name">The stored name, decoded by <see cref="EntryNames.Decode"/>.</param>
    /// <param name="SymlinkTarget">The target 7z lists for a symbolic link, when the format stores one in the listing.</param>
    /// <param name="HardLinkTarget">The archive path a hard link points at.</param>
    /// <param name="IsSymlink">True for a symbolic link, whether or not the listing shows its target.</param>
    /// <param name="RawName">The name as 7z lists it, byte for byte; the UTF-8 of <paramref name="Name"/> when not given.</param>
    /// <param name="Size">The uncompressed size in bytes, when the listing gives one.</param>
    /// <param name="IsExecutable">True when the stored Unix mode has the owner execute bit.</param>
    internal readonly record struct Entry(string Name, bool IsDirectory, string? SymlinkTarget = null, string? HardLinkTarget = null, bool IsSymlink = false, byte[]? RawName = null, long? Size = null, bool IsExecutable = false)
    {
        internal byte[] Raw => RawName ?? Encoding.UTF8.GetBytes(Name);
    }

    /// <summary>Decoded names the bulk extraction has to leave out.</summary>
    internal IReadOnlySet<string> Excluded { get; }

    /// <summary>The entries behind <see cref="Excluded"/>, by position in the archive; one per stored name.</summary>
    internal IReadOnlyList<int> ExcludedIndices { get; }

    /// <summary>Files to extract one at a time, as decoded name and final path.</summary>
    internal IReadOnlyList<(string Name, string Path)> Moved { get; }

    /// <summary>The entries behind <see cref="Moved"/>, in the same order.</summary>
    internal IReadOnlyList<int> MovedIndices { get; }

    /// <summary>Final paths of rewritten folder entries, created directly.</summary>
    internal IReadOnlyList<string> Folders { get; }

    /// <summary>
    /// Symbolic link entries, as decoded name, final path and the target when the listing gave one. None reach
    /// the bulk run: the extractor decides each from its target and either copies the file or drops it.
    /// </summary>
    internal IReadOnlyList<(string Name, string Path, string? Target)> Links { get; }

    /// <summary>The entries behind <see cref="Links"/>, in the same order.</summary>
    internal IReadOnlyList<int> LinkIndices { get; }

    /// <summary>Hard links left out of the bulk run, as decoded name and why.</summary>
    internal IReadOnlyList<(string Name, string Reason)> DroppedHardLinks { get; }

    /// <summary>The entries the bulk run extracts, for <see cref="ExtractedNames.Repair"/>.</summary>
    internal IReadOnlyList<ExtractedNames.Kept> Kept { get; }

    /// <summary>
    /// Entries whose name is not valid UTF-8 and ends in whitespace. Neither a list file nor an argument can
    /// select them, so they always stay in the bulk run, whatever else is true of them.
    /// </summary>
    internal int UnaddressableCount { get; }

    internal ArchiveEntryPath.Rewrites Rewrites { get; }

    /// <summary>True when 7z's own extraction already gives the right result for every entry.</summary>
    internal bool IsPlain => Excluded.Count == 0 && Folders.Count == 0 && Moved.Count == 0 && Links.Count == 0;

    /// <summary>
    /// Where 7z itself extracts an entry: it splits on <c>/</c> only and drops empty, <c>.</c> and
    /// <c>..</c> segments.
    /// </summary>
    internal static string PlacedBy7z(string rawName) =>
        string.Join('/', rawName.Split('/').Where(segment => segment is not ("" or "." or "..")));

    private readonly byte[][] _raws;
    private readonly long?[] _sizes;
    private readonly bool[] _executable;
    private readonly Dictionary<string, int> _lastWithName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nameCounts;

    /// <summary>The number of entries the listing gave.</summary>
    internal int Count => _raws.Length;

    /// <summary>The size the listing gives an entry, if any. Of a name stored more than once, the last entry's.</summary>
    internal long? Size(int index) => _sizes[_lastWithName[Key(_raws[index])]];

    /// <summary>Whether the archive stores an entry with the owner execute bit. Of a name stored more than once, the last entry's.</summary>
    internal bool IsExecutable(int index) => _executable[_lastWithName[Key(_raws[index])]];

    /// <summary>The stored name of an entry, byte for byte.</summary>
    internal byte[] RawName(int index) => _raws[index];

    /// <summary>
    /// Whether the archive stores this name more than once. 7z overwrites on disk in archive order, but
    /// writing one entry to a stream (<c>-so</c>) writes every entry of that name.
    /// </summary>
    internal bool IsRepeated(int index) => _nameCounts[Key(_raws[index])] > 1;

    /// <summary>
    /// The entries stored below <paramref name="index"/>. 7z treats a name given with <c>-i!</c> like
    /// <c>-x</c>, so these come along unless they are excluded too.
    /// </summary>
    internal IEnumerable<int> IndicesBelow(int index)
    {
        var prefix = Encoding.Latin1.GetBytes(Key(_raws[index]).TrimEnd('/') + "/");
        for (var i = 0; i < _raws.Length; i++)
        {
            if (_raws[i].AsSpan().StartsWith(prefix)) yield return i;
        }
    }

    // One character per byte, so a stored name can be a dictionary key whatever its encoding.
    private static string Key(byte[] raw) => Encoding.Latin1.GetString(raw);

    internal ArchiveEntryPlan(IReadOnlyList<Entry> entries)
    {
        _raws = entries.Select(e => e.Raw).ToArray();
        _sizes = entries.Select(e => e.Size).ToArray();
        _executable = entries.Select(e => e.IsExecutable).ToArray();
        for (var i = 0; i < _raws.Length; i++) _lastWithName[Key(_raws[i])] = i;
        _nameCounts = _raws.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        Rewrites = new ArchiveEntryPath.Rewrites();
        var cleaned = entries.Select(e => ArchiveEntryPath.Clean(e.Name)).ToArray();

        // The path an entry gets on disk: cleaned, then with the characters and trailing spaces and dots
        // that can't stay in a name fixed.
        var final = cleaned.Select(EntryNames.FixPath).ToArray();
        var addressable = _raws.Select(raw => EntryNames.IsAddressable(raw)).ToArray();

        // The last file written to a path wins, as it does for plain duplicates. Entries that merely repeat
        // a raw name are left to 7z, which overwrites in archive order.
        var lastForPath = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            // Links never supersede a file: the file at a link's path wins (see ExtractedLinks.Place).
            if (addressable[i] && !entries[i].IsDirectory && !IsLink(entries[i]) && final[i].Length > 0) lastForPath[final[i]] = i;
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var keptNames = new HashSet<string>(StringComparer.Ordinal);
        var keptEntries = new List<ExtractedNames.Kept>();
        var moved = new List<(string, string)>();
        var movedIndices = new List<int>();
        var folders = new List<string>();
        var movedNames = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<(string, string, string?)>();
        var linkIndices = new List<int>();
        var linkPrefixes = entries.Where(e => e.IsSymlink).Select(e => e.Name.TrimEnd('/') + "/").ToArray();
        var droppedHardLinks = new List<(string, string)>();

        void Keep(int i, bool drop = false)
        {
            kept.Add(Key(_raws[i]));
            keptNames.Add(entries[i].Name);
            keptEntries.Add(new ExtractedNames.Kept(_raws[i], i, entries[i].IsDirectory, entries[i].IsSymlink, drop));
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var (name, isDirectory) = (entries[i].Name, entries[i].IsDirectory);
            var rewritten = ArchiveEntryPath.IsRewritten(name, cleaned[i]);
            if (rewritten) Rewrites.Add(name, cleaned[i]);

            if (!addressable[i])
            {
                UnaddressableCount++;

                // Such a link cannot be left out of the run, so 7z creates it under whatever its name says; it
                // is removed again before anything else reads the folder (see ExtractedNames.Repair).
                Keep(i, drop: IsLink(entries[i]));
                continue;
            }

            // Excluding a name also excludes everything below it, so an entry that goes through a link
            // entry can't stay in the bulk run: it is extracted alone, which makes 7z create a real folder.
            var underLink = linkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
            var placedRight = EntryNames.FixPath(PlacedBy7z(name)) == final[i] && !underLink;
            var key = Key(_raws[i]);

            if (final[i].Length == 0)
            {
                // A folder entry for the root itself ("." or "./") has nothing to create.
                if (!(isDirectory && placedRight)) excluded.Add(key);
                continue;
            }

            if (entries[i].IsSymlink)
            {
                excluded.Add(key);
                links.Add((name, final[i], entries[i].SymlinkTarget));
                linkIndices.Add(i);
                continue;
            }

            if (entries[i].HardLinkTarget is { } hardTarget)
            {
                // 7z creates a hard link next to the files it extracted, so one whose target is a kept
                // file cannot reach outside. Any other target gives 7z nothing to link and fails the run.
                if (placedRight && keptNames.Contains(hardTarget))
                {
                    Keep(i);
                }
                else
                {
                    excluded.Add(key);
                    droppedHardLinks.Add((name, $"its target `{hardTarget}` is not an earlier file extracted in place"));
                }

                continue;
            }

            if (isDirectory)
            {
                if (placedRight)
                {
                    Keep(i);
                    continue;
                }

                excluded.Add(key);
                folders.Add(final[i]);
                continue;
            }

            var later = lastForPath[final[i]];
            var superseded = later != i && Key(_raws[later]) != key;
            if (superseded)
            {
                Rewrites.AddOverwritten(name, final[i]);
                excluded.Add(key);
                continue;
            }

            if (placedRight)
            {
                Keep(i);
                continue;
            }

            excluded.Add(key);
            if (movedNames.Add(key))
            {
                moved.Add((name, final[i]));
                movedIndices.Add(i);
            }
        }

        excluded.ExceptWith(kept);
        var excludedIndices = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var key = Key(_raws[i]);
            if (excluded.Contains(key) && seen.Add(key)) excludedIndices.Add(i);
        }

        Excluded = excludedIndices.Select(i => entries[i].Name).ToHashSet(StringComparer.Ordinal);
        ExcludedIndices = excludedIndices;
        Moved = moved;
        MovedIndices = movedIndices;
        Folders = folders;
        Links = links;
        LinkIndices = linkIndices;
        DroppedHardLinks = droppedHardLinks;
        Kept = keptEntries;
    }

    private static bool IsLink(Entry entry) => entry.IsSymlink || entry.HardLinkTarget is not null;
}
