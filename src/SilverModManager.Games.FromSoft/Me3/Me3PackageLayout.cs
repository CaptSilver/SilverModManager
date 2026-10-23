using System.Collections.Frozen;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>
/// Finds the folder of an archive that a me3 package is made of: the one that mirrors the game's archive layout.
/// </summary>
public static class Me3PackageLayout
{
    /// <summary>
    /// The top-level folders of the game's archive layout. A folder that holds one of these, or a
    /// <c>regulation.bin</c>, is a package root.
    /// </summary>
    public static readonly FrozenSet<string> AssetDirs = new[]
    {
        "parts", "chr", "obj", "asset", "menu", "msg", "sfx", "sound", "event",
        "map", "action", "param", "font", "cutscene", "movie", "script", "material",
        "mtd", "remo", "shader", "other", "expression", "facegen",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extensions of files that may sit beside a wrapper folder without disqualifying it: readmes, previews, and
    /// the launcher files a full ModEngine2 archive ships beside its <c>mod/</c> folder.
    /// </summary>
    public static readonly FrozenSet<string> DocExtensions = new[]
    {
        ".txt", ".md", ".pdf", ".png", ".jpg", ".jpeg", ".html", ".url", ".ini", ".exe", ".toml", ".bat",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private const string RegulationFile = "regulation.bin";

    /// <summary>
    /// Looks for the package root among the archive's files, descending through a single wrapper folder when no
    /// stray file sits beside it. On success <paramref name="root"/> is the folder's path inside the archive,
    /// empty when the archive root itself is the package.
    /// </summary>
    public static bool TryFindRoot(IEnumerable<RelativePath> archiveFiles, out RelativePath root)
        => TryFindRoot(Folder.Build(archiveFiles), out root);

    /// <summary>
    /// The names of the archive's top-level folders that are each a complete package. An archive with several
    /// of them is a set of options, and picking one is the user's call.
    /// </summary>
    public static IReadOnlyList<string> ListOptionDirs(IEnumerable<RelativePath> archiveFiles)
    {
        var top = Folder.Build(archiveFiles);
        return top.Folders
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Where(f => TryFindRoot(f.Value, out _))
            .Select(f => f.Key)
            .ToArray();
    }

    private static bool TryFindRoot(Folder folder, out RelativePath root)
    {
        while (true)
        {
            if (folder.Folders.Keys.Any(AssetDirs.Contains) || folder.Files.Any(f => f.Equals(RegulationFile, StringComparison.OrdinalIgnoreCase)))
            {
                root = folder.Path;
                return true;
            }

            var hasStrayFile = folder.Files.Any(f => !DocExtensions.Contains(Path.GetExtension(f)));
            if (folder.Folders.Count != 1 || hasStrayFile)
            {
                root = default;
                return false;
            }

            folder = folder.Folders.Values.Single();
        }
    }

    /// <summary>A folder of the archive, rebuilt from the paths of its files.</summary>
    private sealed class Folder(RelativePath path)
    {
        public RelativePath Path { get; } = path;
        public Dictionary<string, Folder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Files { get; } = [];

        public static Folder Build(IEnumerable<RelativePath> archiveFiles)
        {
            var top = new Folder(RelativePath.Empty);
            foreach (var file in archiveFiles)
            {
                var parts = file.Path.Split('/');
                var folder = top;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (!folder.Folders.TryGetValue(parts[i], out var child))
                    {
                        var childPath = folder.Path == RelativePath.Empty ? parts[i] : $"{folder.Path.Path}/{parts[i]}";
                        child = new Folder(childPath);
                        folder.Folders.Add(parts[i], child);
                    }

                    folder = child;
                }

                folder.Files.Add(parts[^1]);
            }

            return top;
        }
    }
}
