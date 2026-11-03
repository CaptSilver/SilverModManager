using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SilverModManager.Backend.FileExtractor;

/// <summary>
/// Renames what 7z extracted so every name is the one <see cref="EntryNames"/> gives it. 7z writes an
/// unflagged non-UTF-8 name as its raw bytes, which .NET can't open or rename, and keeps a trailing space
/// or dot on a name; neither can be left for the code that reads the folder afterwards. Renames go
/// through libc on byte paths, the only way to address a raw-byte name.
/// </summary>
internal static partial class ExtractedNames
{
    /// <param name="Raw">The name as stored in the archive.</param>
    /// <param name="Index">Position in the archive; of two entries that end up at one path, the higher one stays.</param>
    /// <param name="IsSymlink">True for a symbolic link entry. 7z leaves a 0-byte file where it refused to create one.</param>
    /// <param name="Drop">True for a link entry that could not be kept out of the run: whatever 7z made of it is removed.</param>
    internal readonly record struct Kept(byte[] Raw, int Index, bool IsDirectory, bool IsSymlink = false, bool Drop = false);

    private const int NoSuchFile = 2;
    private const int DirectoryNotEmpty = 39;
    private const int NotALink = 22;

    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static partial int Rename(byte[] oldPath, byte[] newPath);

    [LibraryImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static partial int Unlink(byte[] path);

    [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static partial nint ReadLink(byte[] path, byte[] buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "rmdir", SetLastError = true)]
    private static partial int RemoveDirectory(byte[] path);

    /// <summary>
    /// Renames the entries whose on-disk name differs from the fixed one. Files are moved, not their
    /// folders, so two folders that fix to one name merge; the emptied folders are removed last.
    /// </summary>
    internal static void Repair(string root, IReadOnlyList<Kept> kept, ILogger logger, string archive)
    {
        var entries = Distinct(RestoreNewlines(root, kept));
        var prepared = entries.Select(entry => Prepare(entry)).Where(p => p.Segments.Length > 0).ToArray();

        var rootBytes = Encoding.UTF8.GetBytes(root.TrimEnd('/'));

        // A link 7z refused to create is a 0-byte regular file under its name. Importing it would pass an
        // empty file off as the link's content, so it goes before anything is renamed.
        var placeholders = new HashSet<Prepared>();
        foreach (var p in prepared.Where(p => p.Entry.IsSymlink && !p.Entry.Drop && IsRegularFile(Join(rootBytes, p.RawSegments, p.RawSegments.Length))))
        {
            Check(Unlink(Join(rootBytes, p.RawSegments, p.RawSegments.Length)), p, "removing the placeholder 7z left for a refused link");
            placeholders.Add(p);
            logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: 7z refused to create it", EntryNames.Printable(Encoding.UTF8.GetString(p.Entry.Raw)), archive);
        }

        // A link whose name 7z cannot be given is created wherever its name says, which no rule has judged;
        // it goes, link or hard link alike (unlink removes a link itself and never follows it).
        foreach (var p in prepared.Where(p => p.Entry.Drop))
        {
            Check(Unlink(Join(rootBytes, p.RawSegments, p.RawSegments.Length)), p, "removing the link that 7z could not be told to leave out");
            placeholders.Add(p);
            logger.LogWarning("Dropping link `{Entry}` from archive `{Archive}`: its name cannot be given to 7z, so it could not be left out of the extraction", EntryNames.Printable(Encoding.UTF8.GetString(p.Entry.Raw)), archive);
        }

        prepared = prepared.Where(p => !placeholders.Contains(p)).ToArray();

        // A link never beats a file at the same path (see ExtractedLinks.Place), whatever order they are stored in.
        var winners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in prepared.Where(p => !p.Entry.IsDirectory && !p.Entry.IsSymlink)) winners[p.FinalPath] = Math.Max(winners.GetValueOrDefault(p.FinalPath, -1), p.Entry.Index);

        // Folders that some entry still lives in or is, at their final path: never emptied-out leftovers.
        var livingFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in prepared)
        {
            var finalSegments = p.Segments.Select(Encoding.UTF8.GetBytes).ToArray();
            var folderCount = p.Entry.IsDirectory ? finalSegments.Length : finalSegments.Length - 1;
            for (var depth = 1; depth <= folderCount; depth++) livingFolders.Add(PathKey(finalSegments, depth));
        }

        var oldFolders = new Dictionary<string, (int Depth, byte[] Path)>(StringComparer.Ordinal);
        var report = new List<string>();

        var linkWinners = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in prepared.Where(p => p.Entry.IsSymlink)) linkWinners[p.FinalPath] = Math.Max(linkWinners.GetValueOrDefault(p.FinalPath, -1), p.Entry.Index);

        foreach (var p in prepared.Where(p => p.FirstChange >= 0))
        {
            var oldPath = Join(rootBytes, p.RawSegments, p.RawSegments.Length);
            var finalFull = Path.Combine(root, p.FinalPath);
            report.Add($"`{EntryNames.Printable(Encoding.UTF8.GetString(p.Entry.Raw))}` -> `{EntryNames.Printable(p.FinalPath)}`");

            if (p.Entry.IsDirectory)
            {
                if (!SafeExtraction.TryCreateFolder(root, finalFull)) WarnLinkOnTheWay(logger, p, archive);
            }
            else if (p.Entry.IsSymlink ? winners.ContainsKey(p.FinalPath) || linkWinners[p.FinalPath] != p.Entry.Index : winners[p.FinalPath] != p.Entry.Index)
            {
                Check(Unlink(oldPath), p, "removing the entry a later one replaces");
                logger.LogWarning("Dropping `{Entry}` from archive `{Archive}`: a later entry also extracts to `{Path}`", EntryNames.Printable(Encoding.UTF8.GetString(p.Entry.Raw)), archive, p.FinalPath);
            }
            else
            {
                if (SafeExtraction.TryCreateFolder(root, Path.GetDirectoryName(finalFull) ?? root))
                {
                    Check(Rename(oldPath, NullTerminated(Encoding.UTF8.GetBytes(finalFull))), p, "renaming");
                }
                else
                {
                    Check(Unlink(oldPath), p, "removing the entry whose folder is a link");
                    WarnLinkOnTheWay(logger, p, archive);
                }
            }

            var folderDepth = p.Entry.IsDirectory ? p.RawSegments.Length : p.RawSegments.Length - 1;
            for (var depth = p.FirstChange + 1; depth <= folderDepth; depth++)
            {
                // "a/b/../c" is placed at a/b/c but cleaned to a/c; a/b is then still a folder of the archive.
                if (livingFolders.Contains(PathKey(p.RawSegments, depth))) continue;
                var folder = Join(rootBytes, p.RawSegments, depth);
                oldFolders[Convert.ToHexString(folder)] = (depth, folder);
            }
        }

        foreach (var (_, folder) in oldFolders.Values.OrderByDescending(f => f.Depth))
        {
            if (RemoveDirectory(folder) == 0) continue;
            var errno = Marshal.GetLastPInvokeError();
            if (errno == NoSuchFile) continue;
            var detail = errno == DirectoryNotEmpty ? "it still holds files that are not listed in the archive" : new Win32Exception(errno).Message;
            throw new FileExtractionException($"Could not remove the folder `{EntryNames.Printable(Encoding.UTF8.GetString(folder[..^1]))}` left behind by renaming names in archive `{archive}`: {detail}");
        }

        if (report.Count > 0) logger.LogWarning("Renamed {Count} entry name(s) in archive `{Archive}` to names that work on every platform: {Entries}", report.Count, archive, string.Join("; ", report));
    }

    private static void WarnLinkOnTheWay(ILogger logger, Prepared p, string archive) =>
        logger.LogWarning("Dropping `{Entry}` from archive `{Archive}`: a folder on the way to `{Path}` is a symbolic link", EntryNames.Printable(Encoding.UTF8.GetString(p.Entry.Raw)), archive, p.FinalPath);

    /// <summary>True when the path exists and is not a symbolic link (readlink fails with EINVAL on anything else).</summary>
    private static bool IsRegularFile(byte[] path) =>
        ReadLink(path, new byte[1], 1) == -1 && Marshal.GetLastPInvokeError() == NotALink;

    /// <summary>
    /// 7z's listing prints a newline in a name as an underscore (no other control byte is rewritten), while
    /// extraction writes the real newline to disk. A listed name with an underscore that is not on disk is
    /// matched against the folder's entries with the newline put back, so the rename below finds the file.
    /// </summary>
    private static List<Kept> RestoreNewlines(string root, IReadOnlyList<Kept> kept)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var restored = new List<Kept>(kept.Count);
        foreach (var entry in kept)
        {
            restored.Add(entry.Raw.Contains((byte)'_') ? entry with { Raw = RestoreNewlines(root, entry.Raw, strict) } : entry);
        }

        return restored;
    }

    private static byte[] RestoreNewlines(string root, byte[] raw, UTF8Encoding strict)
    {
        var segments = SplitPlacedBy7z(raw);
        var directory = root;
        var changed = false;
        for (var i = 0; i < segments.Length; i++)
        {
            string name;
            try
            {
                name = strict.GetString(segments[i]);
            }
            catch (DecoderFallbackException)
            {
                // A name that is not UTF-8 can't be walked with .NET; the rename below copes with it as it is.
                break;
            }

            var path = Path.Combine(directory, name);
            if (name.Contains('_') && !Present(path))
            {
                var candidates = Directory.Exists(directory)
                    ? Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Where(n => n is not null && n != name && n.Replace('\n', '_') == name).ToArray()
                    : [];
                if (candidates.Length == 1 && candidates[0] is { } real)
                {
                    segments[i] = Encoding.UTF8.GetBytes(real);
                    path = Path.Combine(directory, real);
                    changed = true;
                }
            }

            directory = path;
        }

        if (!changed) return raw;
        var joined = new List<byte>();
        foreach (var segment in segments)
        {
            if (joined.Count > 0) joined.Add((byte)'/');
            joined.AddRange(segment);
        }

        return [.. joined];
    }

    private static bool Present(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private sealed record Prepared(Kept Entry, byte[][] RawSegments, string[] Segments, int FirstChange)
    {
        internal string FinalPath => string.Join('/', Segments);
    }

    /// <summary>One entry per stored name: a repeated name is one file on disk, written by the last of them.</summary>
    private static List<Kept> Distinct(IReadOnlyList<Kept> kept)
    {
        var byName = new Dictionary<string, Kept>(StringComparer.Ordinal);
        foreach (var entry in kept)
        {
            var key = Convert.ToHexString(entry.Raw);
            if (!byName.TryGetValue(key, out var earlier) || earlier.Index < entry.Index) byName[key] = entry;
        }

        return byName.Values.OrderBy(e => e.Index).ToList();
    }

    private static Prepared Prepare(Kept entry)
    {
        var rawSegments = SplitPlacedBy7z(entry.Raw);

        // The path the rest of the extraction gives the entry, so a backslash, a drive letter or a ".." that
        // 7z left in the name is resolved the same way (see ArchiveEntryPlan).
        var segments = EntryNames.FixPath(ArchiveEntryPath.Clean(EntryNames.Decode(entry.Raw))).Split('/', StringSplitOptions.RemoveEmptyEntries);

        var firstChange = -1;
        for (var i = 0; i < segments.Length && i < rawSegments.Length; i++)
        {
            if (Encoding.UTF8.GetBytes(segments[i]).AsSpan().SequenceEqual(rawSegments[i])) continue;
            firstChange = i;
            break;
        }

        // Fewer cleaned segments than placed ones ("a/b/.." is "a" on disk as a/b) changes the first extra one.
        if (firstChange < 0 && segments.Length != rawSegments.Length) firstChange = Math.Min(segments.Length, rawSegments.Length);

        return new Prepared(entry, rawSegments, segments, firstChange);
    }

    /// <summary>The segments 7z writes for a stored name: it splits on <c>/</c> and drops empty, <c>.</c> and <c>..</c> segments.</summary>
    private static byte[][] SplitPlacedBy7z(byte[] raw)
    {
        var segments = new List<byte[]>();
        var start = 0;
        for (var i = 0; i <= raw.Length; i++)
        {
            if (i < raw.Length && raw[i] != (byte)'/') continue;
            var segment = raw.AsSpan(start, i - start);
            if (!(segment.Length == 0 || segment.SequenceEqual("."u8) || segment.SequenceEqual(".."u8))) segments.Add(segment.ToArray());
            start = i + 1;
        }

        return [.. segments];
    }

    private static byte[] Join(byte[] root, byte[][] segments, int count)
    {
        var path = new List<byte>(root);
        for (var i = 0; i < count; i++)
        {
            path.Add((byte)'/');
            path.AddRange(segments[i]);
        }

        path.Add(0);
        return [.. path];
    }

    /// <summary>The first <paramref name="count"/> segments joined by <c>/</c>, one character per byte so any name is a valid key.</summary>
    private static string PathKey(byte[][] segments, int count) =>
        string.Join('/', segments.Take(count).Select(segment => Encoding.Latin1.GetString(segment)));

    private static byte[] NullTerminated(byte[] bytes) => [.. bytes, 0];

    private static void Check(int result, Prepared entry, string action)
    {
        if (result == 0) return;
        var errno = Marshal.GetLastPInvokeError();

        // An entry 7z did not write (a FIFO in a tar, say) has nothing to rename.
        if (errno == NoSuchFile) return;
        throw new FileExtractionException($"Failed {action} `{EntryNames.Printable(Encoding.UTF8.GetString(entry.Entry.Raw))}` to `{entry.FinalPath}`: {new Win32Exception(errno).Message}");
    }
}
