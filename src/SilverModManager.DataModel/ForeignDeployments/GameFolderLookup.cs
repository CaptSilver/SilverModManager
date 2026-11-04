using SilverModManager.Sdk.IO;

namespace SilverModManager.DataModel.ForeignDeployments;

/// <summary>
/// Finds the files another manager's records name inside a game folder, and decides whether a found file really
/// is inside it once links are followed.
/// </summary>
internal sealed class GameFolderLookup
{
    private readonly string _root;
    private readonly Lazy<string?> _realRoot;

    public GameFolderLookup(string root)
    {
        _root = root;
        _realRoot = new Lazy<string?>(() => LinkResolution.RealPath(Path.GetFullPath(root)));
    }

    /// <summary>
    /// Folders a mod type's files may sit in, best guess first: the folder a record's Windows <c>targetPath</c> names
    /// below the game's own folder name, the longest tail of it that exists under the game folder, and the game
    /// folder itself. The path is only a hint (it can come from another machine), so none of it is trusted.
    /// </summary>
    public IReadOnlyList<string> CandidateRoots(string targetPath)
    {
        var candidates = new List<string>();
        var parts = targetPath.Split('\\', '/').Where(part => part.Length > 0 && !part.EndsWith(':')).ToArray();
        if (parts.All(part => part != ".." && part != "." && !part.Contains(':')))
        {
            var gameFolderName = Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar));
            var nameAt = Array.FindLastIndex(parts, part => part.Equals(gameFolderName, StringComparison.OrdinalIgnoreCase));
            if (nameAt >= 0 && nameAt < parts.Length - 1) candidates.Add(Path.Combine(_root, Path.Combine(parts[(nameAt + 1)..])));

            for (var i = 0; i < parts.Length; i++)
            {
                var tail = Path.Combine(_root, Path.Combine(parts[i..]));
                if (Directory.Exists(tail)) candidates.Add(tail);
            }
        }

        candidates.Add(_root);
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The file at <paramref name="segments"/> below <paramref name="folder"/>, spelled the way the disk has it, or
    /// null. Vortex records come from a case-insensitive file system, so a name that differs only in case is the same file.
    /// </summary>
    public string? Find(string folder, string[] segments)
    {
        var current = folder;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            var exact = Path.Combine(current, segments[i]);
            if (last ? File.Exists(exact) : Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            if (!Directory.Exists(current)) return null;
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => Path.GetFileName(entry).Equals(segments[i], StringComparison.OrdinalIgnoreCase)
                                         && (last ? File.Exists(entry) : Directory.Exists(entry)));
            if (match is null) return null;
            current = match;
        }

        return current;
    }

    /// <summary>
    /// Whether the file, with every link on its way followed, is still inside the game folder. A link loop counts as outside.
    /// </summary>
    public bool IsInsideGameFolder(string file)
    {
        var real = LinkResolution.RealPath(Path.GetFullPath(file));
        return real is not null && _realRoot.Value is { } realRoot
                                && real.StartsWith(realRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
