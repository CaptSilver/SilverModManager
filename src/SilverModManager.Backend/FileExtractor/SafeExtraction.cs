namespace SilverModManager.Backend.FileExtractor;

/// <summary>
/// The file operations extraction does on paths an archive influences. A symbolic link at, or on the way
/// to, a path would make a write, a rename or a chmod land wherever the link points, so folders are created
/// one component at a time without crossing a link, and files are written to a fresh sibling and renamed
/// into place: a rename replaces a link at the target instead of following it.
/// </summary>
internal static class SafeExtraction
{
    /// <summary>True when <paramref name="path"/> is a symbolic link itself, whatever it points at.</summary>
    internal static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>
    /// Creates <paramref name="folder"/> and the folders above it that are missing, all below
    /// <paramref name="root"/>. Returns false, creating nothing past it, when an existing component below the
    /// root is a symbolic link. The root itself may be reached through links (<c>/home</c> is one on ostree).
    /// </summary>
    internal static bool TryCreateFolder(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder);
        if (relative == ".") return true;
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;

        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current)) return false;
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
        }

        return true;
    }

    /// <summary>A name next to <paramref name="target"/> that nothing can be using, for writing before the rename.</summary>
    internal static string SiblingTemp(string target) =>
        Path.Combine(Path.GetDirectoryName(target) ?? throw new InvalidOperationException($"`{target}` has no parent folder"), $".{Guid.NewGuid():N}.smm-tmp");

    /// <summary>Renames <paramref name="temp"/> over <paramref name="target"/>, after giving it <paramref name="mode"/> when one is given.</summary>
    internal static void Commit(string temp, string target, UnixFileMode? mode = null)
    {
        if (mode is { } m) File.SetUnixFileMode(temp, m);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>, replacing whatever is there, a link included.</summary>
    internal static void CopyReplacing(string source, string target, UnixFileMode? mode = null)
    {
        var temp = SiblingTemp(target);
        try
        {
            File.Copy(source, temp);
            Commit(temp, target, mode);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
