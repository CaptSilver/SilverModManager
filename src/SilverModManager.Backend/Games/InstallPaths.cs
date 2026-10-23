using NexusMods.Paths;

namespace NexusMods.Backend.Games;

/// <summary>
/// Compares install folders as folders rather than as strings.
/// </summary>
/// <remarks>
/// Steam writes the home library into libraryfolders.vdf as /var/home/user/..., while $HOME says
/// /home/user/... and /home is a symlink to /var/home. A string comparison calls those two installs.
/// </remarks>
internal static class InstallPaths
{
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Whether <paramref name="stored"/> (a path kept in the database) and <paramref name="actual"/> name the same
    /// folder once every symlink along them is followed.
    /// </summary>
    public static bool AreSameFolder(string stored, AbsolutePath actual, Func<string, string?>? readLink = null)
    {
        readLink ??= ReadLinkOnDisk;
        var storedFull = actual.FileSystem.FromUnsanitizedFullPath(stored).GetFullPath();
        var actualFull = actual.GetFullPath();
        if (string.Equals(storedFull, actualFull, StringComparison.Ordinal)) return true;

        return string.Equals(Resolve(storedFull, readLink), Resolve(actualFull, readLink), StringComparison.Ordinal);
    }

    /// <summary>
    /// Follows the symlinks on every component of the path. A link that cannot be read (the drive it is on is
    /// unplugged, the folder is not ours to read) is left as it is spelled, and a chain of links that does
    /// not end gives up and returns the path as given.
    /// </summary>
    private static string Resolve(string fullPath, Func<string, string?> readLink)
    {
        var resolved = fullPath;

        // Every pass resolves the first symlink it meets and starts over on the rewritten path,
        // so a link whose target runs through another link is followed all the way.
        for (var pass = 0; pass <= MaxLinkHops; pass++)
        {
            var rewritten = RewriteFirstLink(resolved, readLink);
            if (rewritten is null) return resolved;
            resolved = rewritten;
        }

        return fullPath;
    }

    private static string? RewriteFirstLink(string fullPath, Func<string, string?> readLink)
    {
        var separator = Path.DirectorySeparatorChar;
        var parts = fullPath.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        var current = separator.ToString();

        for (var i = 0; i < parts.Length; i++)
        {
            current = current == separator.ToString() ? current + parts[i] : current + separator + parts[i];

            string? target;
            try
            {
                target = readLink(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (target is null) continue;

            var parent = Path.GetDirectoryName(current) ?? separator.ToString();
            var absoluteTarget = Path.GetFullPath(target, parent);
            var rest = string.Join(separator, parts.Skip(i + 1));
            return rest.Length == 0 ? absoluteTarget : Path.Combine(absoluteTarget, rest);
        }

        return null;
    }

    private static string? ReadLinkOnDisk(string path)
    {
        // A folder that does not exist has no link target, which is what an absent drive looks like too.
        return new DirectoryInfo(path).LinkTarget;
    }
}
