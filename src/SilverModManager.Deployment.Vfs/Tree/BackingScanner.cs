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
    /// itself is not listed. Symlinks are followed for the listing only: the handle they would be
    /// attached to is closed on return, so a tree that serves these entries is built from
    /// <see cref="Scan(DirectoryHandle)"/> instead.
    /// </summary>
    IReadOnlyList<BackingEntry> Scan(AbsolutePath root);

    /// <summary>
    /// The same listing, read through an open directory. Every symlinked directory or file followed is
    /// resolved here, once, and attached to <paramref name="root"/> so the tree can serve it after a
    /// mount covers the path. Run this before mounting.
    /// </summary>
    IReadOnlyList<BackingEntry> Scan(DirectoryHandle root);

    /// <summary>
    /// The same fields for one file, or null if it is not there. <see cref="BackingEntry.Path"/> is
    /// just the file's name.
    /// </summary>
    BackingEntry? Stat(AbsolutePath file);

    /// <summary>
    /// Like <see cref="Stat(AbsolutePath)"/> for <paramref name="file"/> below <paramref name="root"/>, read
    /// through the descriptor. A symlink that the scan did not attach reads as not there.
    /// </summary>
    BackingEntry? Stat(DirectoryHandle root, RelativePath file);
}

/// <summary>
/// Walks a directory with one <c>fstatat</c> call per entry. Symlinks are followed, as <c>stat</c>
/// does; a directory reached twice (a symlink back up the tree) is listed once, so a loop cannot run
/// away. Sockets, pipes and devices are skipped, and so are links whose target is gone.
/// </summary>
public sealed class BackingScanner : IBackingScanner
{
    private const int ENOENT = 2;
    private const int ELOOP = 40;
    private const int ENOTDIR = 20;

    public IReadOnlyList<BackingEntry> Scan(AbsolutePath root)
    {
        using var handle = DirectoryHandle.Open(root);
        return Scan(handle);
    }

    public IReadOnlyList<BackingEntry> Scan(DirectoryHandle root)
    {
        var errno = LibcDirectory.TryStatAt(root.Handle, string.Empty, followSymlinks: true, out var rootStat);
        if (errno != 0) throw new IOException($"stat of `{root.Root}` failed with errno {errno}.");
        if (!rootStat.IsDirectory) throw new DirectoryNotFoundException($"`{root.Root}` is not a directory.");

        var entries = new List<BackingEntry>();
        var visited = new HashSet<(ulong Device, ulong Inode)> { (rootStat.Device, rootStat.Inode) };
        Walk(root, root, string.Empty, RelativePath.Empty, entries, visited);
        return entries;
    }

    public BackingEntry? Stat(AbsolutePath file)
    {
        var errno = LibcStat.TryStat(file.ToString(), out var buffer);
        if (errno is ENOENT or ELOOP) return null;
        if (errno != 0) throw new IOException($"stat of `{file}` failed with errno {errno}.");
        return ToEntry(file.FileName, buffer);
    }

    public BackingEntry? Stat(DirectoryHandle root, RelativePath file)
    {
        if (root.Stat(file) is not { } buffer || !buffer.IsRegularFile && !buffer.IsDirectory) return null;
        return ToEntry(file.FileName, buffer);
    }

    // Lists `relativeInHandle` below `handle`; `root` is the handle the paths in the result are relative to.
    private static void Walk(DirectoryHandle root, DirectoryHandle handle, string relativeInHandle, RelativePath relative, List<BackingEntry> entries, HashSet<(ulong, ulong)> visited)
    {
        foreach (var (name, _, isSymlink) in LibcDirectory.Enumerate(handle.Handle, relativeInHandle))
        {
            var inHandle = relativeInHandle.Length == 0 ? name : $"{relativeInHandle}/{name}";
            var errno = LibcDirectory.TryStatAt(handle.Handle, inHandle, followSymlinks: true, out var buffer);
            if (errno is ENOENT or ELOOP or ENOTDIR) continue;
            if (errno != 0) throw new IOException($"stat of `{inHandle}` below `{handle.Root}` failed with errno {errno}.");

            var childRelative = relative.Join(RelativePath.FromUnsanitizedInput(name));
            if (buffer.IsDirectory)
            {
                if (!visited.Add((buffer.Device, buffer.Inode))) continue;
                entries.Add(ToEntry(childRelative, buffer));

                if (isSymlink)
                {
                    // Resolved here, while the path still leads where it did when the folder was chosen.
                    var target = handle.OpenFollowing(inHandle);
                    root.Attach(childRelative, target);
                    Walk(root, target, string.Empty, childRelative, entries, visited);
                }
                else
                {
                    Walk(root, handle, inHandle, childRelative, entries, visited);
                }
            }
            else if (buffer.IsRegularFile)
            {
                entries.Add(ToEntry(childRelative, buffer));
                if (isSymlink) AttachFileLink(root, handle, inHandle, childRelative);
            }
        }
    }

    private static void AttachFileLink(DirectoryHandle root, DirectoryHandle handle, string inHandle, RelativePath childRelative)
    {
        var errno = LibcDirectory.TryRealPath(handle.Root.Combine(RelativePath.FromUnsanitizedInput(inHandle)).ToString(), out var resolved);
        if (errno != 0) throw new IOException($"Resolving the symlink `{inHandle}` below `{handle.Root}` failed with errno {errno}.");

        var target = FileSystem.Shared.FromUnsanitizedFullPath(resolved);
        root.AttachFile(childRelative, DirectoryHandle.Open(target.Parent), target.FileName);
    }

    private static BackingEntry ToEntry(RelativePath path, LibcStat.Buffer buffer) =>
        new(path, buffer.IsDirectory, buffer.Mode, Size.From((ulong)buffer.Size), buffer.MTime, buffer.CTime, buffer.Device, buffer.Inode);
}
