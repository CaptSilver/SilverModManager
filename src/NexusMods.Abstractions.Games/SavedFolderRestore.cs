namespace NexusMods.Abstractions.Games;

/// <summary>
/// Puts a backup folder back in place of a link to nowhere. Another tool can replace a game's save folder with a
/// link into its own storage and keep the real folder beside it under a backup name; when that tool is removed
/// the link dangles and the game sees no saves.
/// </summary>
/// <remarks>
/// Only ever touches the one link (removed with <see cref="File.Delete(string)"/>, so never what it pointed at)
/// and the one backup (moved, never copied or deleted).
/// </remarks>
public static class SavedFolderRestore
{
    /// <summary>
    /// True when <paramref name="path"/> is a link whose target does not exist. The link is not followed.
    /// </summary>
    public static bool IsDanglingLink(string path) => TryGetDanglingTarget(path, out _);

    /// <summary>
    /// As <see cref="IsDanglingLink"/>, also returning where the link points.
    /// </summary>
    public static bool TryGetDanglingTarget(string path, out string target)
    {
        target = "";
        if (LinkTargetOf(path) is not { } linkTarget) return false;
        if (TargetExists(path)) return false;

        target = linkTarget;
        return true;
    }

    /// <summary>
    /// File.Exists is true for a link to nothing on Unix, so the end of the link chain is checked instead.
    /// </summary>
    private static bool TargetExists(string linkPath)
    {
        try
        {
            if (new FileInfo(linkPath).ResolveLinkTarget(returnFinalTarget: true) is not { } final) return false;
            return Directory.Exists(final.FullName) || (File.Exists(final.FullName) && LinkTargetOf(final.FullName) is null);
        }
        catch (IOException)
        {
            // A link loop never resolves.
            return false;
        }
    }

    /// <summary>
    /// Where the link at <paramref name="path"/> points, read without following it, or null when it is not a link.
    /// </summary>
    public static string? LinkTargetOf(string path) => new DirectoryInfo(path).LinkTarget ?? new FileInfo(path).LinkTarget;

    /// <summary>
    /// The siblings of <paramref name="linkPath"/> named <c>&lt;name&gt;.*backup*</c>, whatever they are.
    /// </summary>
    public static IReadOnlyList<string> FindBackups(string linkPath)
    {
        var parent = Path.GetDirectoryName(linkPath);
        var name = Path.GetFileName(linkPath);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || !Directory.Exists(parent)) return [];

        var prefix = name + ".";
        return Directory.EnumerateFileSystemEntries(parent)
            .Where(entry =>
            {
                var entryName = Path.GetFileName(entry);
                return entryName.StartsWith(prefix, StringComparison.Ordinal)
                       && entryName.AsSpan(prefix.Length).Contains("backup", StringComparison.OrdinalIgnoreCase);
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// True when <paramref name="path"/> is a real folder (not a link) that holds at least one file.
    /// </summary>
    public static bool IsRestorableBackup(string path)
    {
        if (!Directory.Exists(path) || LinkTargetOf(path) is not null) return false;

        // Links inside the backup are skipped so the walk never leaves it.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(path, "*", options).Any();
    }

    /// <summary>
    /// Replaces the dangling link at <paramref name="linkPath"/> with <paramref name="backupPath"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A precondition no longer holds, or the file system refused a change. The message says what state the two
    /// paths were left in; the backup itself is never touched by a failed restore.
    /// </exception>
    public static void Restore(string linkPath, string backupPath)
    {
        if (!IsDanglingLink(linkPath))
            throw new InvalidOperationException($"'{linkPath}' is no longer a link to a missing folder, so it was left alone.");

        if (!IsRestorableBackup(backupPath))
            throw new InvalidOperationException($"'{backupPath}' is no longer a folder with files in it, so nothing was restored.");

        if (!string.Equals(Path.GetDirectoryName(linkPath), Path.GetDirectoryName(backupPath), StringComparison.Ordinal))
            throw new InvalidOperationException($"'{backupPath}' is not next to '{linkPath}', so nothing was restored.");

        try
        {
            File.Delete(linkPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"'{linkPath}' could not be removed ({e.Message}), so nothing was restored.", e);
        }

        // Directory.Move onto an existing path would fail or merge, so make sure the link really is gone first.
        if (Path.Exists(linkPath) || LinkTargetOf(linkPath) is not null)
            throw new InvalidOperationException($"'{linkPath}' could not be removed, so nothing was restored.");

        try
        {
            Directory.Move(backupPath, linkPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The broken link '{linkPath}' was removed, but '{backupPath}' could not be moved into its place ({e.Message}). " +
                $"Your files are still in '{backupPath}'.", e);
        }
    }
}
