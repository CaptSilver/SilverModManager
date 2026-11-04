using SilverModManager.Sdk.Errors;
namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Folds a game's folder on the host into the copy of it inside the game's Proton prefix, then sends the host
/// folder to the trash. The game only reads the prefix copy, so anything that lives only in the host folder (old
/// saves, ini edits) is brought across first.
/// </summary>
/// <remarks>
/// Files are only ever added to the prefix folder: one that exists in both places keeps the prefix version, and
/// nothing in the prefix is overwritten or deleted. Links inside the host folder are skipped instead of followed, so
/// the walk never leaves it; they travel to the trash with the folder.
/// </remarks>
public static class HostFolderMove
{
    /// <summary>
    /// Copies what <paramref name="hostFolder"/> has and <paramref name="prefixFolder"/> lacks, then trashes the host folder.
    /// </summary>
    /// <exception cref="HostFolderMoveRefusedException">
    /// A precondition does not hold or a copy failed. The host folder is untouched, and files already copied stay
    /// in the prefix folder, where they do no harm.
    /// </exception>
    public static void Run(string hostFolder, string prefixFolder, ITrash trash)
    {
        Validate(hostFolder, prefixFolder);

        try
        {
            Directory.CreateDirectory(prefixFolder);
            CopyMissing(hostFolder, prefixFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HostFolderMoveRefusedException($"The files in {MarkdownText.Code(hostFolder)} could not be copied into {MarkdownText.Code(prefixFolder)} ({MarkdownText.Inline(e.Message)}), so the folder was left where it is.", e);
        }

        trash.MoveToTrash(hostFolder);
    }

    private static void Validate(string hostFolder, string prefixFolder)
    {
        if (!Directory.Exists(hostFolder))
            throw new HostFolderMoveRefusedException($"{MarkdownText.Code(hostFolder)} is no longer there, so there is nothing to move.");

        if (SavedFolderRestore.LinkTargetOf(hostFolder) is not null)
            throw new HostFolderMoveRefusedException($"{MarkdownText.Code(hostFolder)} is a link to another folder, so it was left alone.");

        // The prefix folder is often reached through a link (Documents shared with the host's, say). Compared as
        // written, the two would look apart while being the same files, and trashing "the host copy" would take the game's.
        string host, prefix;
        try
        {
            host = Path.TrimEndingDirectorySeparator(ResolvedPath.Of(hostFolder));
            prefix = Path.TrimEndingDirectorySeparator(ResolvedPath.Of(prefixFolder));
        }
        catch (IOException e)
        {
            throw new HostFolderMoveRefusedException($"The links on the way to {MarkdownText.Code(prefixFolder)} could not be followed ({MarkdownText.Inline(e.Message)}), so nothing was moved.", e);
        }

        if (IsSameOrInside(host, prefix) || IsSameOrInside(prefix, host))
            throw new HostFolderMoveRefusedException($"{MarkdownText.Code(hostFolder)} and {MarkdownText.Code(prefixFolder)} overlap, so nothing was moved.");

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefixFolder)));
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new HostFolderMoveRefusedException($"The folder {MarkdownText.Code(parent)} does not exist, so {MarkdownText.Code(hostFolder)} was left where it is. Start the game once to create it.");
    }

    private static bool IsSameOrInside(string path, string folder)
        => path == folder || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static void CopyMissing(string from, string to)
    {
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) continue;

            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                Directory.CreateDirectory(target);
                CopyMissing(directory.FullName, target);
            }
            else if (!Path.Exists(target))
            {
                File.Copy(entry.FullName, target);
            }
        }
    }
}
