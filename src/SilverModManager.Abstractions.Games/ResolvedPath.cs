namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Follows the links in a path, including the ones in its parent folders, which <see cref="Path.GetFullPath(string)"/>
/// leaves alone.
/// </summary>
public static class ResolvedPath
{
    private const int MaxLinks = 40;

    /// <summary>
    /// The absolute path of <paramref name="path"/> with every link on the way replaced by what it points at. Parts that
    /// do not exist are kept as written, so a folder that is yet to be created still gets the right parent.
    /// </summary>
    /// <exception cref="IOException">The links loop, or there are more of them than a path can sensibly hold.</exception>
    public static string Of(string path)
    {
        var pending = new Stack<string>(Segments(Path.GetFullPath(path)).Reverse());
        var current = Path.DirectorySeparatorChar.ToString();
        var followed = 0;

        while (pending.TryPop(out var segment))
        {
            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var candidate = Path.Combine(current, segment);
            if (SavedFolderRestore.LinkTargetOf(candidate) is not { } target)
            {
                current = candidate;
                continue;
            }

            if (++followed > MaxLinks) throw new IOException($"'{path}' passes through too many links.");

            // A relative target is read from the folder the link sits in. Restarting from the root keeps ".." honest.
            foreach (var part in Segments(Path.GetFullPath(target, current)).Reverse()) pending.Push(part);
            current = Path.DirectorySeparatorChar.ToString();
        }

        return current;
    }

    private static string[] Segments(string fullPath)
        => fullPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
}
