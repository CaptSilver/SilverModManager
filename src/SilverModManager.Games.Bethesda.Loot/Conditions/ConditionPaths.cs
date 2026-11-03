using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

// Follows loot-condition-interpreter's function/path.rs, with case-insensitive lookup added because the
// masterlists spell paths the way Windows would find them.

/// <summary>
/// Finds the files a condition names inside a game's Data folder, and lists its folders.
/// </summary>
public static class ConditionPaths
{
    private const string GhostSuffix = ".ghost";

    /// <summary>
    /// The path on disk for <paramref name="relativePath"/> under <paramref name="dataDirectory"/>. The exact
    /// spelling wins; otherwise each segment is matched against its folder's entries ignoring case, and with
    /// <paramref name="tryGhost"/> the same is repeated for <c>&lt;path&gt;.ghost</c>. When nothing exists the
    /// result is the exact join (the ghosted one with <paramref name="tryGhost"/>), so callers can test it as
    /// they would any other path.
    /// </summary>
    public static string Resolve(string dataDirectory, string relativePath, bool tryGhost)
    {
        var exact = Path.Combine(dataDirectory, relativePath);
        if (Exists(exact)) return exact;

        var segments = Segments(relativePath);
        if (FindIgnoringCase(dataDirectory, segments) is { } found) return found;
        if (!tryGhost) return exact;

        var ghosted = exact + GhostSuffix;
        if (Exists(ghosted)) return ghosted;

        if (segments.Length > 0)
        {
            segments[^1] += GhostSuffix;
            if (FindIgnoringCase(dataDirectory, segments) is { } foundGhost) return foundGhost;
        }

        return ghosted;
    }

    /// <summary>
    /// The names in the folder <paramref name="parent"/> of <paramref name="dataDirectory"/> (files and folders),
    /// with <c>.ghost</c> taken off plugin names. Empty when the folder does not exist or cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ListDirectory(string dataDirectory, string parent, LootGame game)
    {
        var directory = Resolve(dataDirectory, parent, tryGhost: false);
        if (!Directory.Exists(directory)) return [];

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

    private static string[] Segments(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();

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
