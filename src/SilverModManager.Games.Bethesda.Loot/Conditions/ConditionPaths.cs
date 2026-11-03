using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

// Follows loot-condition-interpreter's function/path.rs, with case-insensitive lookup added because the
// masterlists spell paths the way Windows would find them.

/// <summary>
/// Finds the files a condition names inside a game's Data folder, and lists its folders. Both <c>/</c> and
/// <c>\\</c> separate path segments.
/// </summary>
public static class ConditionPaths
{
    private const string GhostSuffix = ".ghost";

    /// <summary>
    /// The path on disk for <paramref name="relativePath"/> under <paramref name="dataDirectory"/>, or null when the
    /// path is refused. <c>..</c> is resolved lexically, so a masterlist can reach the game's own files from Data
    /// (<c>..\\TESV.exe</c>), but the result has to stay inside the game root, the parent of the Data folder; a
    /// rooted path or one that climbs above that root is refused. The exact spelling wins; otherwise each segment is
    /// matched against its folder's entries ignoring case, and with <paramref name="tryGhost"/> the same is repeated
    /// for <c>&lt;path&gt;.ghost</c>. When nothing exists the result is the exact join (the ghosted one with
    /// <paramref name="tryGhost"/>), so callers can test it as they would any other path.
    /// </summary>
    public static string? Resolve(string dataDirectory, string relativePath, bool tryGhost)
    {
        if (Normalise(dataDirectory, relativePath) is not var (start, segments)) return null;

        var exact = segments.Length == 0 ? start : Path.Combine([start, .. segments]);
        if (Exists(exact)) return exact;

        if (FindIgnoringCase(start, segments) is { } found) return found;
        if (!tryGhost) return exact;

        var ghosted = exact + GhostSuffix;
        if (Exists(ghosted)) return ghosted;

        if (segments.Length > 0)
        {
            var ghostSegments = (string[])segments.Clone();
            ghostSegments[^1] += GhostSuffix;
            if (FindIgnoringCase(start, ghostSegments) is { } foundGhost) return foundGhost;
        }

        return ghosted;
    }

    /// <summary>The game's folder, the parent of <paramref name="dataDirectory"/>, which no condition path may leave.</summary>
    public static string GameRoot(string dataDirectory) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory))) ?? Path.GetFullPath(dataDirectory);

    /// <summary>
    /// Whether <paramref name="name"/> is a bare file name: not empty, no separator, not rooted, no NUL, not <c>.</c> or
    /// <c>..</c>. Plugin lookups take nothing else.
    /// </summary>
    public static bool IsPlainFileName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.AsSpan().IndexOfAny('/', '\\', '\0') < 0;

    /// <summary>
    /// Opens <paramref name="path"/> for reading when it is a regular file, or returns null. A pipe, socket or device is
    /// not one, since opening it can block; a symbolic link counts when it ends at a regular file, wherever that is
    /// (the symlink deployment backend links into the staging store). The type is checked on the handle the stream
    /// reads, so every read of a path a condition named goes through this and not through a second open.
    /// </summary>
    public static FileStream? OpenRegularFile(string path) => RegularFiles.Open(path);

    /// <summary>
    /// The names in the folder <paramref name="parent"/> of <paramref name="dataDirectory"/> (files and folders),
    /// with <c>.ghost</c> taken off plugin names. Empty when the folder does not exist or cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ListDirectory(string dataDirectory, string parent, LootGame game)
    {
        var directory = Resolve(dataDirectory, parent, tryGhost: false);
        if (directory is null || !Directory.Exists(directory)) return [];

        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Select(entry => NormaliseFileName(game, Path.GetFileName(entry)))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Whether a missing <paramref name="path"/> may be stored as <c>&lt;path&gt;.ghost</c> for this game.</summary>
    public static bool ShouldTryGhost(LootGame game, string path) =>
        game.AllowsGhostedPlugins() && HasUnghostedPluginExtension(game, path);

    /// <summary>Whether the path ends in a plugin extension of the game, optionally followed by <c>.ghost</c> where ghosting is allowed.</summary>
    public static bool HasPluginExtension(LootGame game, string path)
    {
        if (game.AllowsGhostedPlugins() && EndsWithGhost(path))
            return HasUnghostedPluginExtension(game, path[..^GhostSuffix.Length]);

        return HasUnghostedPluginExtension(game, path);
    }

    /// <summary>The file name with a trailing <c>.ghost</c> removed when what is left is a plugin name.</summary>
    public static string NormaliseFileName(LootGame game, string name)
    {
        if (!game.AllowsGhostedPlugins() || !EndsWithGhost(name)) return name;

        var stem = name[..^GhostSuffix.Length];
        return HasUnghostedPluginExtension(game, stem) ? stem : name;
    }

    private static bool EndsWithGhost(string path) => path.EndsWith(GhostSuffix, StringComparison.OrdinalIgnoreCase);

    private static bool HasUnghostedPluginExtension(LootGame game, string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        if (extension.Length == 0) return false;

        return extension.Equals(".esp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".esm", StringComparison.OrdinalIgnoreCase)
            || (game.SupportsLightPlugins() && extension.Equals(".esl", StringComparison.OrdinalIgnoreCase))
            || (game == LootGame.OpenMW
                && (extension.Equals(".omwaddon", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".omwgame", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".omwscripts", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// The folder to walk from and the segments to follow, with <c>.</c> and <c>..</c> folded away. Null when the path
    /// is rooted, holds a NUL or leaves the game root. The folder is the Data folder, or the game root when the path climbed out
    /// of Data.
    /// </summary>
    private static (string Start, string[] Segments)? Normalise(string dataDirectory, string relativePath)
    {
        if (relativePath.Contains('\0')) return null;

        // Masterlist paths come from Windows, where "..\\TESV.exe" names the game's executable.
        relativePath = relativePath.Replace('\\', '/');
        if (relativePath.StartsWith('/') || (relativePath.Length >= 2 && relativePath[1] == ':' && char.IsAsciiLetter(relativePath[0])))
            return null;

        var kept = new List<string>();
        var climbed = 0;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment != "..") kept.Add(segment);
            else if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);
            else if (++climbed > 1) return null;
        }

        var start = climbed == 0 ? dataDirectory : GameRoot(dataDirectory);
        return (start, kept.ToArray());
    }

    /// <summary>Walks the segments from <paramref name="root"/>, taking the exact name when present and else the first entry that matches ignoring case.</summary>
    private static string? FindIgnoringCase(string root, string[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            var exact = Path.Combine(current, segment);
            if (Exists(exact))
            {
                current = exact;
                continue;
            }

            if (!Directory.Exists(current)) return null;

            string? match;
            try
            {
                match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (match is null) return null;
            current = match;
        }

        return current;
    }
}
