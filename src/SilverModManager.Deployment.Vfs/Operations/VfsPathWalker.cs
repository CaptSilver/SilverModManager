namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Resolves slash-separated paths against inode-based operations the way a kernel does: one
/// <see cref="IVfsOperations.Lookup"/> per segment, starting at the root inode. A path-based
/// binding reuses it so it sees exactly the errors and lookups a low-level one would.
/// </summary>
public sealed class VfsPathWalker
{
    private readonly IVfsOperations _operations;
    private readonly Action<ulong, string>? _onLookup;

    /// <param name="onLookup">Called with (parent, name) before each Lookup.</param>
    public VfsPathWalker(IVfsOperations operations, Action<ulong, string>? onLookup = null)
    {
        _operations = operations;
        _onLookup = onLookup;
    }

    /// <summary>
    /// The entry at <paramref name="path"/>, or the first error. The empty path is the root.
    /// </summary>
    public VfsResult<VfsEntry> Walk(string path)
    {
        var current = RootEntry();
        if (!current.Ok) return current;

        foreach (var segment in Segments(path))
        {
            _onLookup?.Invoke(current.Value.Ino, segment);
            current = _operations.Lookup(current.Value.Ino, segment);
            if (!current.Ok) return current;
            // A negative entry carries Ino 0 and no error.
            if (current.Value.Ino == 0) return VfsResult<VfsEntry>.Failure(Errno.ENOENT);
        }
        return current;
    }

    /// <summary>
    /// Resolves everything but the last segment. For creating, deleting and renaming, where the
    /// kernel hands the operation a parent inode and a name.
    /// </summary>
    public VfsResult<(ulong Parent, string Name)> WalkParent(string path)
    {
        var segments = Segments(path);
        if (segments.Length == 0) return VfsResult<(ulong, string)>.Failure(Errno.EINVAL);

        var parent = Walk(string.Join('/', segments[..^1]));
        return parent.Ok
            ? VfsResult<(ulong, string)>.Success((parent.Value.Ino, segments[^1]))
            : VfsResult<(ulong, string)>.Failure(parent.Error!.Value);
    }

    private VfsResult<VfsEntry> RootEntry()
    {
        var attr = _operations.GetAttr(IVfsOperations.RootInode);
        return attr.Ok
            ? VfsResult<VfsEntry>.Success(new VfsEntry(IVfsOperations.RootInode, attr.Value, TimeSpan.Zero, TimeSpan.Zero))
            : VfsResult<VfsEntry>.Failure(attr.Error!.Value);
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
