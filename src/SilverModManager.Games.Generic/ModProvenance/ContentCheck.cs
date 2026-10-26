namespace SilverModManager.Games.Generic.ModProvenance;

/// <param name="Path">Relative to the folder, with <c>/</c> separators.</param>
public sealed record LocalFile(string Path, long Size);

/// <summary>
/// Decides whether a folder is what an archive contains, by file names and sizes alone. A same-named mod page is not
/// enough: two mods can share a name, and only the right one holds these files at these sizes.
/// </summary>
public static class ContentCheck
{
    private const int MaxFiles = 20_000;

    /// <summary>The manager's own bookkeeping file, which is never part of an archive.</summary>
    private const string ManagerFile = "meta.ini";

    public static IReadOnlyList<LocalFile> LocalFiles(string folder)
    {
        var files = new List<LocalFile>();
        if (!Directory.Exists(folder)) return files;

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (files.Count >= MaxFiles) break;
                files.Add(new LocalFile(Path.GetRelativePath(folder, path).Replace('\\', '/'), new FileInfo(path).Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read fully is compared as far as it was read; the caller treats a partial one as a reject.
            files.Add(new LocalFile("\0unreadable", -1));
        }

        return files;
    }

    /// <summary>True when every file of the folder (but its manager's meta.ini) is in the listing with the same size.</summary>
    public static bool Accepts(IReadOnlyList<LocalFile> local, IReadOnlyList<ArchiveEntry> listing)
    {
        var files = local.Where(f => !f.Path.Equals(ManagerFile, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return false;

        var bySize = listing.Select(e => (Path: e.Path.Replace('\\', '/').ToLowerInvariant(), e.Size)).ToArray();
        return files.All(f =>
        {
            var wanted = f.Path.ToLowerInvariant();
            return bySize.Any(e => e.Size == f.Size && (e.Path == wanted || e.Path.EndsWith("/" + wanted, StringComparison.Ordinal)));
        });
    }
}
