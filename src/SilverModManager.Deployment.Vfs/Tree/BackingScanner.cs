using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// What one <c>stat(2)</c> says about an entry in the game's own directory. Device and inode are
/// there for fingerprinting: a file replaced in place keeps its path but not its inode.
/// </summary>
public readonly record struct BackingEntry(
    RelativePath Path,
    bool IsDirectory,
    uint Mode,
    Size Size,
    DateTimeOffset MTime,
    DateTimeOffset CTime,
    ulong Device,
    ulong Inode);

public interface IBackingScanner
{
    /// <summary>
    /// Every directory and regular file under <paramref name="root"/>, paths relative to it. The root
    /// itself is not listed.
    /// </summary>
    IReadOnlyList<BackingEntry> Scan(AbsolutePath root);

    /// <summary>
    /// The same fields for one file, or null if it is not there. <see cref="BackingEntry.Path"/> is
    /// just the file's name.
    /// </summary>
    BackingEntry? Stat(AbsolutePath file);
}

/// <summary>
/// Walks a directory with one <see cref="LibcStat"/> call per entry. Symlinks are followed, as
/// <c>stat</c> does; a directory reached twice (a symlink back up the tree) is listed once, so a loop
/// cannot run away. Sockets, pipes and devices are skipped, and so are links whose target is gone.
/// </summary>
public sealed class BackingScanner : IBackingScanner
{
    private const int ENOENT = 2;
    private const int ELOOP = 40;

    public IReadOnlyList<BackingEntry> Scan(AbsolutePath root)
    {
        var rootStat = StatOrThrow(root.ToString());
        if (!rootStat.IsDirectory) throw new DirectoryNotFoundException($"`{root}` is not a directory.");

        var entries = new List<BackingEntry>();
        var visited = new HashSet<(ulong Device, ulong Inode)> { (rootStat.Device, rootStat.Inode) };
        Walk(root.ToString(), RelativePath.Empty, entries, visited);
        return entries;
    }

    public BackingEntry? Stat(AbsolutePath file)
    {
        var errno = LibcStat.TryStat(file.ToString(), out var buffer);
        if (errno is ENOENT or ELOOP) return null;
        if (errno != 0) throw new IOException($"stat of `{file}` failed with errno {errno}.");
        return ToEntry(file.FileName, buffer);
    }

    private static void Walk(string directory, RelativePath relative, List<BackingEntry> entries, HashSet<(ulong, ulong)> visited)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false };
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
        {
            var name = System.IO.Path.GetFileName(path);
            var errno = LibcStat.TryStat(path, out var buffer);
            if (errno is ENOENT or ELOOP) continue;
            if (errno != 0) throw new IOException($"stat of `{path}` failed with errno {errno}.");

            var childRelative = relative.Join(RelativePath.FromUnsanitizedInput(name));
            if (buffer.IsDirectory)
            {
                if (!visited.Add((buffer.Device, buffer.Inode))) continue;
                entries.Add(ToEntry(childRelative, buffer));
                Walk(path, childRelative, entries, visited);
            }
            else if (buffer.IsRegularFile)
            {
                entries.Add(ToEntry(childRelative, buffer));
            }
        }
    }

    private static LibcStat.Buffer StatOrThrow(string path)
    {
        var errno = LibcStat.TryStat(path, out var buffer);
        if (errno == ENOENT) throw new DirectoryNotFoundException($"`{path}` does not exist.");
        if (errno != 0) throw new IOException($"stat of `{path}` failed with errno {errno}.");
        return buffer;
    }

    private static BackingEntry ToEntry(RelativePath path, LibcStat.Buffer buffer) =>
        new(path, buffer.IsDirectory, buffer.Mode, Size.From((ulong)buffer.Size), buffer.MTime, buffer.CTime, buffer.Device, buffer.Inode);
}
