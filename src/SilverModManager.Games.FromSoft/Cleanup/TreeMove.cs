namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>Moves a folder out of the game folder to a place that may be on another filesystem.</summary>
internal static class TreeMove
{
    /// <summary>
    /// A rename when both folders are on one filesystem. The Steam library and the app's data folder often are not,
    /// and a rename between them fails with the source untouched, so the tree is copied instead.
    /// </summary>
    public static void Move(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
            return;
        }
        catch (IOException) when (Directory.Exists(source) && !Directory.Exists(target))
        {
        }

        CopyThenRemove(source, target);
    }

    /// <summary>
    /// Copies the tree, links as links, and removes the source only once the copy is complete. A copy that fails
    /// part-way is removed again, so the source is the one copy either way.
    /// </summary>
    internal static void CopyThenRemove(string source, string target)
    {
        try
        {
            Copy(new DirectoryInfo(source), new DirectoryInfo(target));
        }
        catch
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            throw;
        }

        // A recursive delete unlinks links inside the tree without following them.
        Directory.Delete(source, recursive: true);
    }

    private static void Copy(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            var to = Path.Combine(target.FullName, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo) Directory.CreateSymbolicLink(to, link);
                else File.CreateSymbolicLink(to, link);
            }
            else if (entry is DirectoryInfo directory) Copy(directory, new DirectoryInfo(to));
            else ((FileInfo)entry).CopyTo(to);
        }
    }
}
