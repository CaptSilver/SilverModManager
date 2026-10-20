using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// The merged view of the game directory as an inode tree with case-insensitive lookup. Nothing
/// mounts it yet and it is read-only: operations that would change it fail with EROFS until the
/// tree has somewhere to put written files.
/// </summary>
public sealed class VfsTree : IVfsOperations
{
    // 0100444 and 040555: nobody writes through a read-only tree.
    private const uint RegularFileMode = 0x8000 | 0x124;
    private const uint DirectoryMode = 0x4000 | 0x16D;
    private const ulong DirectorySize = 4096;

    private readonly object _gate = new();
    private readonly InodeTable _inodes = new();
    private readonly Dictionary<ulong, VfsNode> _byIno = new();
    private readonly VfsNode _root;
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;

    private VfsTree()
    {
        _root = NewDirectory(IVfsOperations.RootInode, IVfsOperations.RootInode, "");
        _byIno[_root.Ino] = _root;
    }

    /// <summary>
    /// Builds the tree from <paramref name="sources"/> in order. Directory spellings come from
    /// the first insertion; a later source for the same name replaces the node, never its spelling.
    /// A <see cref="VfsSourceKind.Hidden"/> source removes what is there, and does nothing if
    /// nothing is.
    /// </summary>
    public static VfsTree Build(IReadOnlyList<VfsFileSource> sources)
    {
        var tree = new VfsTree();
        foreach (var source in sources) tree.Insert(source);
        return tree;
    }

    private void Insert(VfsFileSource source)
    {
        var segments = source.Path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new ArgumentException("A source needs a non-empty path.", nameof(source));

        var directory = _root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var key = new VfsNameKey(segments[i]);
            if (directory.Children!.TryGetValue(key, out var child) && child.IsDirectory)
            {
                directory = child;
                continue;
            }

            // Hiding something below a directory that is not there removes nothing.
            if (source.Kind == VfsSourceKind.Hidden) return;

            // A file standing where a directory is needed gives way, keeping its spelling.
            var name = child?.DisplayName ?? segments[i];
            if (child is not null) Unindex(child);
            var created = NewDirectory(_inodes.GetOrAdd(directory.Ino, key), directory.Ino, name);
            directory.Children[key] = created;
            _byIno[created.Ino] = created;
            directory = created;
        }

        var leafKey = new VfsNameKey(segments[^1]);
        directory.Children!.TryGetValue(leafKey, out var existing);

        if (source.Kind == VfsSourceKind.Hidden)
        {
            if (existing is null) return;
            Unindex(existing);
            directory.Children.Remove(leafKey);
            return;
        }

        if (existing is not null) Unindex(existing);
        var file = new VfsNode
        {
            Ino = _inodes.GetOrAdd(directory.Ino, leafKey),
            ParentIno = directory.Ino,
            DisplayName = existing?.DisplayName ?? segments[^1],
            IsDirectory = false,
            Source = source,
        };
        directory.Children[leafKey] = file;
        _byIno[file.Ino] = file;
    }

    private VfsNode NewDirectory(ulong ino, ulong parent, string name) => new()
    {
        Ino = ino,
        ParentIno = parent,
        DisplayName = name,
        IsDirectory = true,
        Children = new Dictionary<VfsNameKey, VfsNode>(),
    };

    // Drops a replaced or hidden node and everything below it from the inode index; the
    // (parent, name) numbers stay in the table.
    private void Unindex(VfsNode node)
    {
        _byIno.Remove(node.Ino);
        if (node.Children is null) return;
        foreach (var child in node.Children.Values) Unindex(child);
    }

    private VfsEntry EntryFor(VfsNode node)
    {
        var attr = AttrFor(node);
        return new VfsEntry(node.Ino, attr, TimeSpan.Zero, TimeSpan.Zero);
    }

    private VfsAttr AttrFor(VfsNode node)
    {
        if (!node.IsDirectory)
            return new VfsAttr(node.Ino, RegularFileMode, node.Source!.Value.Size.Value, _created, _created, 1);

        var subdirectories = (uint)node.Children!.Values.Count(c => c.IsDirectory);
        return new VfsAttr(node.Ino, DirectoryMode, DirectorySize, _created, _created, 2 + subdirectories);
    }

    public VfsResult<VfsEntry> Lookup(ulong parent, string name)
    {
        lock (_gate)
        {
            if (!_byIno.TryGetValue(parent, out var directory)) return VfsResult<VfsEntry>.Failure(Errno.ENOENT);
            if (!directory.IsDirectory) return VfsResult<VfsEntry>.Failure(Errno.ENOTDIR);
            return directory.Children!.TryGetValue(new VfsNameKey(name), out var child)
                ? VfsResult<VfsEntry>.Success(EntryFor(child))
                : VfsResult<VfsEntry>.Failure(Errno.ENOENT);
        }
    }

    // Inode numbers are kept for the life of the tree, so there is no per-lookup count to release.
    public void Forget(ulong ino, ulong lookups) { }

    public VfsResult<VfsAttr> GetAttr(ulong ino)
    {
        lock (_gate)
        {
            return _byIno.TryGetValue(ino, out var node)
                ? VfsResult<VfsAttr>.Success(AttrFor(node))
                : VfsResult<VfsAttr>.Failure(Errno.ENOENT);
        }
    }

    public VfsResult<VfsDirPage> ReadDirPlus(ulong ino, ulong offset, int maxEntries)
    {
        lock (_gate)
        {
            if (!_byIno.TryGetValue(ino, out var directory)) return VfsResult<VfsDirPage>.Failure(Errno.ENOENT);
            if (!directory.IsDirectory) return VfsResult<VfsDirPage>.Failure(Errno.ENOTDIR);

            // Offsets are positions in the name-sorted listing, so a page can resume where the last ended.
            var entries = directory.Children!.Values
                .OrderBy(c => c.DisplayName, StringComparer.Ordinal)
                .Select((c, index) => new VfsDirEntry(c.DisplayName, EntryFor(c), (ulong)index + 1))
                .Skip((int)Math.Min(offset, int.MaxValue))
                .Take(Math.Max(maxEntries, 0))
                .ToArray();
            return VfsResult<VfsDirPage>.Success(new VfsDirPage(entries));
        }
    }

    public VfsResult<VfsOpenFile> Open(ulong ino, VfsOpenFlags flags)
    {
        VfsFileSource source;
        lock (_gate)
        {
            if (!_byIno.TryGetValue(ino, out var node)) return VfsResult<VfsOpenFile>.Failure(Errno.ENOENT);
            if (node.IsDirectory) return VfsResult<VfsOpenFile>.Failure(Errno.EISDIR);
            source = node.Source!.Value;
        }

        if ((flags & (VfsOpenFlags.Write | VfsOpenFlags.Truncate | VfsOpenFlags.Append)) != 0)
            return VfsResult<VfsOpenFile>.Failure(Errno.EROFS);
        if (source.RealPath is not { } realPath) return VfsResult<VfsOpenFile>.Failure(Errno.EIO);

        try
        {
            var handle = File.OpenHandle(realPath.ToString(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return VfsResult<VfsOpenFile>.Success(new VfsOpenFile
            {
                Ino = ino,
                Handle = handle,
                RealPath = realPath,
                KeepCache = true,
            });
        }
        catch (FileNotFoundException) { return VfsResult<VfsOpenFile>.Failure(Errno.ENOENT); }
        catch (DirectoryNotFoundException) { return VfsResult<VfsOpenFile>.Failure(Errno.ENOENT); }
        catch (UnauthorizedAccessException) { return VfsResult<VfsOpenFile>.Failure(Errno.EACCES); }
        catch (IOException) { return VfsResult<VfsOpenFile>.Failure(Errno.EIO); }
    }

    public VfsResult<VfsReadReply> Read(VfsOpenFile file, ulong offset, int size)
    {
        if (size < 0) return VfsResult<VfsReadReply>.Failure(Errno.EINVAL);
        var length = (ulong)RandomAccess.GetLength(file.Handle);
        var available = offset >= length ? 0 : length - offset;
        return VfsResult<VfsReadReply>.Success(new VfsReadReply(file.Handle, offset, (int)Math.Min((ulong)size, available)));
    }

    public VfsResult Release(VfsOpenFile file)
    {
        file.Handle.Dispose();
        return VfsResult.Success;
    }

    public VfsResult<VfsStatFs> StatFs()
    {
        lock (_gate)
        {
            return VfsResult<VfsStatFs>.Success(new VfsStatFs(DirectorySize, 0, 0, (ulong)_byIno.Count));
        }
    }

    // Every way of changing the tree is refused until written files have somewhere to go.
    public VfsResult<VfsAttr> SetAttr(ulong ino, VfsSetAttr change) => VfsResult<VfsAttr>.Failure(Errno.EROFS);
    public VfsResult<(VfsEntry Entry, VfsOpenFile File)> Create(ulong parent, string name, uint mode, VfsOpenFlags flags) => VfsResult<(VfsEntry, VfsOpenFile)>.Failure(Errno.EROFS);
    public VfsResult<int> Write(VfsOpenFile file, ulong offset, ReadOnlySpan<byte> data) => VfsResult<int>.Failure(Errno.EROFS);
    public VfsResult<VfsEntry> MkDir(ulong parent, string name, uint mode) => VfsResult<VfsEntry>.Failure(Errno.EROFS);
    public VfsResult Unlink(ulong parent, string name) => VfsResult.Failure(Errno.EROFS);
    public VfsResult RmDir(ulong parent, string name) => VfsResult.Failure(Errno.EROFS);
    public VfsResult Rename(ulong parent, string name, ulong newParent, string newName) => VfsResult.Failure(Errno.EROFS);
}
