using Microsoft.Win32.SafeHandles;
using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// The filesystem as the kernel sees it: inode based and shaped after <c>fuse_lowlevel_ops</c>, so a
/// low-level binding maps the calls one to one and a path-based binding walks paths with one
/// <see cref="Lookup"/> per component. Calls are synchronous and thread-safe. Failures are returned
/// as an <see cref="Errno"/>, never thrown: ENOENT on a negative lookup is the normal protocol.
/// </summary>
public interface IVfsOperations
{
    const ulong RootInode = 1;

    /// <summary>
    /// A result with <c>Ino</c> 0 is a negative entry the kernel may cache for its TTL.
    /// </summary>
    VfsResult<VfsEntry> Lookup(ulong parent, string name);

    void Forget(ulong ino, ulong lookups);
    VfsResult<VfsAttr> GetAttr(ulong ino);
    VfsResult<VfsAttr> SetAttr(ulong ino, VfsSetAttr change);

    /// <summary>
    /// Entries after <paramref name="offset"/> (0 starts the listing), at most <paramref name="maxEntries"/>.
    /// Each entry's <see cref="VfsDirEntry.NextOffset"/> resumes after it; an empty page ends the listing.
    /// <c>.</c> and <c>..</c> are the binding's job.
    /// </summary>
    VfsResult<VfsDirPage> ReadDirPlus(ulong ino, ulong offset, int maxEntries);

    VfsResult<VfsOpenFile> Open(ulong ino, VfsOpenFlags flags);
    VfsResult<(VfsEntry Entry, VfsOpenFile File)> Create(ulong parent, string name, uint mode, VfsOpenFlags flags);
    VfsResult<VfsReadReply> Read(VfsOpenFile file, ulong offset, int size);
    VfsResult<int> Write(VfsOpenFile file, ulong offset, ReadOnlySpan<byte> data);
    VfsResult Release(VfsOpenFile file);
    VfsResult<VfsEntry> MkDir(ulong parent, string name, uint mode);
    VfsResult Unlink(ulong parent, string name);
    VfsResult RmDir(ulong parent, string name);
    VfsResult Rename(ulong parent, string name, ulong newParent, string newName);
    VfsResult<VfsStatFs> StatFs();
}

public readonly record struct VfsEntry(ulong Ino, VfsAttr Attr, TimeSpan EntryTtl, TimeSpan AttrTtl);

public readonly record struct VfsAttr(ulong Ino, uint Mode, ulong Size, DateTimeOffset MTime, DateTimeOffset CTime, uint NLink);

public readonly record struct VfsSetAttr(ulong? Size, DateTimeOffset? MTime, uint? Mode);

[Flags]
public enum VfsOpenFlags
{
    Read = 1,
    Write = 2,
    Truncate = 4,
    Append = 8,
}

/// <summary>
/// An open file. Reads are answered with <see cref="Handle"/> so the binding can splice from the
/// descriptor and no bytes pass through managed memory on the hot path.
/// </summary>
public sealed class VfsOpenFile
{
    public required ulong Ino { get; init; }
    public required SafeFileHandle Handle { get; init; }
    public required AbsolutePath RealPath { get; init; }
    public bool Writable { get; init; }
    public bool KeepCache { get; init; }
    public bool DirectIo { get; init; }
}

public readonly record struct VfsReadReply(SafeFileHandle Handle, ulong Offset, int Length);

public readonly record struct VfsDirEntry(string Name, VfsEntry Entry, ulong NextOffset);

public readonly record struct VfsDirPage(IReadOnlyList<VfsDirEntry> Entries);

public readonly record struct VfsStatFs(ulong BlockSize, ulong Blocks, ulong BlocksFree, ulong Files);

/// <summary>
/// The errno values the operations return; numbers are the Linux ones.
/// </summary>
public enum Errno
{
    ENOENT = 2,
    EIO = 5,
    EACCES = 13,
    EEXIST = 17,
    ENOTDIR = 20,
    EISDIR = 21,
    EINVAL = 22,
    ENOSYS = 38,
    EROFS = 30,
    ENOTEMPTY = 39,
}

public readonly record struct VfsResult(Errno? Error)
{
    public bool Ok => Error is null;

    public static VfsResult Success => new(null);
    public static VfsResult Failure(Errno error) => new(error);
}

public readonly record struct VfsResult<T>(T? Value, Errno? Error)
{
    public bool Ok => Error is null;

    public static VfsResult<T> Success(T value) => new(value, null);
    public static VfsResult<T> Failure(Errno error) => new(default, error);
}
