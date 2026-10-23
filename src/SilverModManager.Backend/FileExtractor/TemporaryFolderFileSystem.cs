using System.IO.MemoryMappedFiles;
using NexusMods.Paths;

namespace SilverModManager.FileExtractor;

/// <summary>
/// File system view that recreates missing parent directories of a root folder before a
/// file is created inside it.
/// </summary>
/// <remarks>
/// <see cref="TemporaryFileManager"/> makes its folder once, in the constructor, and
/// <c>CreateFile</c> only hands out a path. If the folder is deleted afterwards (a tmp
/// cleaner, the user, another process sharing the state directory), the first write to
/// such a path fails with <see cref="DirectoryNotFoundException"/>. The manager's methods
/// can't be overridden, but every path it returns is bound to the file system it was
/// built with, so the repair happens here.
/// </remarks>
internal sealed class TemporaryFolderFileSystem : BaseFileSystem
{
    private readonly IFileSystem _inner;
    private readonly string _rootFullPath;

    // Takes the wrapped file system's OS so paths are sanitized the same way on both sides.
    public TemporaryFolderFileSystem(IFileSystem inner, AbsolutePath root)
        : base(inner.OS, new Dictionary<AbsolutePath, AbsolutePath>(), new Dictionary<KnownPath, AbsolutePath>(), convertCrossPlatformPaths: false)
    {
        _inner = inner;
        _rootFullPath = root.GetFullPath();
    }

    /// <summary>
    /// Re-expresses a path from this file system in the wrapped one, and the other way round.
    /// </summary>
    private AbsolutePath ToInner(AbsolutePath path) => _inner.FromUnsanitizedFullPath(path.GetFullPath());
    private AbsolutePath FromInner(AbsolutePath path) => FromUnsanitizedFullPath(path.GetFullPath());

    private void EnsureParent(AbsolutePath path)
    {
        var inner = ToInner(path);
        var parent = inner.Parent;
        if (!IsInsideRoot(inner) || _inner.DirectoryExists(parent)) return;
        _inner.CreateDirectory(parent);
    }

    private bool IsInsideRoot(AbsolutePath innerPath)
    {
        var full = innerPath.GetFullPath();
        return full.StartsWith(_rootFullPath, StringComparison.Ordinal)
               && full.Length > _rootFullPath.Length
               && (full[_rootFullPath.Length] == '/' || full[_rootFullPath.Length] == '\\');
    }

    public override IFileSystem CreateOverlayFileSystem(Dictionary<AbsolutePath, AbsolutePath> pathMappings, Dictionary<KnownPath, AbsolutePath> knownPathMappings, bool convertCrossPlatformPaths = false)
        => _inner.CreateOverlayFileSystem(pathMappings, knownPathMappings, convertCrossPlatformPaths);

    public override AbsolutePath GetKnownPath(KnownPath knownPath) => FromInner(_inner.GetKnownPath(knownPath));

    public override int ReadBytesRandomAccess(AbsolutePath path, Span<byte> bytes, long offset)
        => _inner.ReadBytesRandomAccess(ToInner(path), bytes, offset);

    public override Task<int> ReadBytesRandomAccessAsync(AbsolutePath path, Memory<byte> bytes, long offset, CancellationToken cancellationToken = default)
        => _inner.ReadBytesRandomAccessAsync(ToInner(path), bytes, offset, cancellationToken);

    public override void SetUnixFileMode(AbsolutePath absolutePath, UnixFileMode flags)
        => _inner.SetUnixFileMode(ToInner(absolutePath), flags);

    public override UnixFileMode GetUnixFileMode(AbsolutePath absolutePath)
        => _inner.GetUnixFileMode(ToInner(absolutePath));

    protected override IFileEntry InternalGetFileEntry(AbsolutePath path) => _inner.GetFileEntry(ToInner(path));

    protected override IDirectoryEntry InternalGetDirectoryEntry(AbsolutePath path) => _inner.GetDirectoryEntry(ToInner(path));

    protected override IEnumerable<AbsolutePath> InternalEnumerateFiles(AbsolutePath directory, string pattern, bool recursive)
        => _inner.EnumerateFiles(ToInner(directory), pattern, recursive).Select(FromInner);

    protected override IEnumerable<AbsolutePath> InternalEnumerateDirectories(AbsolutePath directory, string pattern, bool recursive)
        => _inner.EnumerateDirectories(ToInner(directory), pattern, recursive).Select(FromInner);

    protected override IEnumerable<IFileEntry> InternalEnumerateFileEntries(AbsolutePath directory, string pattern, bool recursive)
        => _inner.EnumerateFileEntries(ToInner(directory), pattern, recursive);

    protected override Stream InternalOpenFile(AbsolutePath path, FileMode mode, FileAccess access, FileShare share)
    {
        if (mode is not (FileMode.Open or FileMode.Truncate)) EnsureParent(path);
        return _inner.OpenFile(ToInner(path), mode, access, share);
    }

    protected override void InternalCreateDirectory(AbsolutePath path) => _inner.CreateDirectory(ToInner(path));

    protected override bool InternalDirectoryExists(AbsolutePath path) => _inner.DirectoryExists(ToInner(path));

    protected override void InternalDeleteDirectory(AbsolutePath path, bool recursive) => _inner.DeleteDirectory(ToInner(path), recursive);

    protected override bool InternalFileExists(AbsolutePath path) => _inner.FileExists(ToInner(path));

    protected override void InternalDeleteFile(AbsolutePath path) => _inner.DeleteFile(ToInner(path));

    protected override void InternalMoveFile(AbsolutePath source, AbsolutePath dest, bool overwrite)
    {
        EnsureParent(dest);
        _inner.MoveFile(ToInner(source), ToInner(dest), overwrite);
    }

    protected override MemoryMappedFileHandle InternalCreateMemoryMappedFile(AbsolutePath absPath, FileMode mode, MemoryMappedFileAccess access, ulong fileSize)
    {
        if (mode is not (FileMode.Open or FileMode.Truncate)) EnsureParent(absPath);
        return _inner.CreateMemoryMappedFile(ToInner(absPath), mode, access, fileSize);
    }
}
