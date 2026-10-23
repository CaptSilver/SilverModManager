using System.Diagnostics;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// The merged view of the game directory as an inode tree with case-insensitive lookup. Nothing
/// mounts it yet. Built with a <see cref="StagingDirectory"/>, every change lands there: a file from
/// the backing directory or the loadout is copied into staging the first time it is written, created
/// files and directories are made there, and names that are deleted or renamed away are recorded as
/// whiteouts. The backing directory and the loose store are only ever opened for reading. Built without
/// one the tree is read-only and changes fail with EROFS.
/// </summary>
public sealed class VfsTree : IVfsOperations
{
    // Served files that were not scanned (staged ones) are 0644; directories are 0755.
    private const uint RegularFileMode = 0x8000 | 0x1A4;
    private const uint DirectoryMode = 0x4000 | 0x1ED;
    private const ulong DirectorySize = 4096;

    private readonly object _gate = new();
    private readonly InodeTable _inodes = new();
    private readonly Dictionary<ulong, VfsNode> _byIno = new();
    private readonly VfsNode _root;
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
    private readonly VfsCachePolicy _policy;
    private StagingDirectory? _staging;
    private readonly Dictionary<string, RelativePath> _whiteouts = new(StringComparer.OrdinalIgnoreCase);

    // Every spelling Lookup was asked for, per directory and folded name. The kernel caches a
    // negative or positive entry per exact spelling, so a change has to drop all of them.
    private const int MaxSpellingsPerName = 8;
    private readonly Dictionary<(ulong Parent, VfsNameKey Key), List<string>> _spellings = new();

    /// <summary>
    /// Told about every change the kernel's caches would not see on their own. Null until a mount sets
    /// it after the session is up; the mount clears it again on unmount.
    /// </summary>
    public IVfsInvalidationSink? Invalidation { get; set; }

    private VfsTree(VfsCachePolicy policy)
    {
        _policy = policy;
        _root = NewDirectory(IVfsOperations.RootInode, IVfsOperations.RootInode, "", lower: true);
        _byIno[_root.Ino] = _root;
    }

    /// <summary>
    /// Builds the tree from the game's own directory, then lays <paramref name="sources"/> over it
    /// in order. Directory spellings come from the first insertion; a later source for the same name
    /// replaces the node, never its spelling. A <see cref="VfsSourceKind.Hidden"/> source removes
    /// what is there, and does nothing if nothing is.
    /// </summary>
    /// <param name="backingRoot">The directory <paramref name="backing"/> was scanned from; backing files are served from under it.</param>
    /// <param name="backing">What the scanner found under <paramref name="backingRoot"/>.</param>
    public static VfsTree Build(AbsolutePath backingRoot, IReadOnlyList<BackingEntry> backing, IReadOnlyList<VfsFileSource> sources, VfsCachePolicy policy)
        => Build(backingRoot, backing, sources, null, policy);

    public static VfsTree Build(AbsolutePath backingRoot, IReadOnlyList<BackingEntry> backing, IReadOnlyList<VfsFileSource> sources, StagingDirectory? staging, VfsCachePolicy policy)
    {
        var tree = new VfsTree(policy) { _staging = staging };
        foreach (var entry in backing) tree.InsertBacking(backingRoot, entry);
        foreach (var source in sources) tree.Insert(source);
        if (staging is not null) tree.LayerStaging(staging);
        return tree;
    }

    // Whiteouts hide what the lower layers provide; what the game has staged goes on top of that, so
    // a name that was deleted and then created again shows the new file.
    private void LayerStaging(StagingDirectory staging)
    {
        foreach (var whiteout in staging.ReadWhiteouts())
        {
            _whiteouts[whiteout.ToString()] = whiteout;
            Insert(new VfsFileSource(whiteout, VfsSourceKind.Hidden, null, Size.Zero, Hash.Zero));
        }

        foreach (var entry in staging.Scan())
        {
            if (entry.IsDirectory)
            {
                var directory = _root;
                foreach (var segment in entry.Path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries))
                    directory = EnsureDirectory(directory, segment, lower: false);
            }
            else
            {
                Insert(new VfsFileSource(entry.Path, VfsSourceKind.Staged, entry.RealPath, entry.Size, Hash.Zero, FromStaging: true));
            }
        }
    }

    private void InsertBacking(AbsolutePath root, BackingEntry entry)
    {
        var segments = entry.Path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new ArgumentException("A backing entry needs a non-empty path.", nameof(entry));

        if (!entry.IsDirectory)
        {
            Insert(new VfsFileSource(entry.Path, VfsSourceKind.Backing, root.Combine(entry.Path), entry.Size, Hash.Zero), entry);
            return;
        }

        var directory = _root;
        foreach (var segment in segments) directory = EnsureDirectory(directory, segment, lower: true);
    }

    // The child directory called name, made if it is not there. A file standing in its place gives way, keeping its spelling.
    private VfsNode EnsureDirectory(VfsNode parent, string segment, bool lower)
    {
        var key = new VfsNameKey(segment);
        if (parent.Children!.TryGetValue(key, out var child) && child.IsDirectory) return child;

        var name = child?.DisplayName ?? segment;
        if (child is not null) Unindex(child);
        var created = NewDirectory(_inodes.GetOrAdd(parent.Ino, key), parent.Ino, name, lower);
        parent.Children[key] = created;
        _byIno[created.Ino] = created;
        return created;
    }

    private void Insert(VfsFileSource source, BackingEntry? scanned = null)
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

            directory = EnsureDirectory(directory, segments[i], lower: !source.FromStaging);
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
            Backing = scanned,
            Shadows = source.FromStaging && existing is not null,
        };
        directory.Children[leafKey] = file;
        _byIno[file.Ino] = file;
    }

    private VfsNode NewDirectory(ulong ino, ulong parent, string name, bool lower) => new()
    {
        Lower = lower,
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
        return new VfsEntry(node.Ino, attr, _policy.EntryTtl, _policy.AttrTtl);
    }

    private VfsAttr AttrFor(VfsNode node)
    {
        // A staged file changes under the tree, so its numbers are read from disk each time.
        if (!node.IsDirectory && node.Source!.Value is { FromStaging: true, RealPath: { } staged }
            && LibcStat.TryStat(staged.ToString(), out var stat) == 0)
            return new VfsAttr(node.Ino, stat.Mode, (ulong)stat.Size, stat.MTime, stat.CTime, 1);

        if (node.Backing is { } scanned)
            return new VfsAttr(node.Ino, scanned.Mode, scanned.Size.Value, scanned.MTime, scanned.CTime, 1);

        if (!node.IsDirectory)
        {
            var source = node.Source!.Value;
            var (mtime, ctime) = TimesOf(source);
            return new VfsAttr(node.Ino, RegularFileMode, source.Size.Value, mtime, ctime, 1);
        }

        var subdirectories = (uint)node.Children!.Values.Count(c => c.IsDirectory);
        return new VfsAttr(node.Ino, DirectoryMode, DirectorySize, _created, _created, 2 + subdirectories);
    }

    // A staged file's times are the real file's. One with no real file behind it (or one that
    // vanished) reports when the tree was built.
    private (DateTimeOffset MTime, DateTimeOffset CTime) TimesOf(VfsFileSource source)
    {
        if (source.RealPath is not { } real) return (_created, _created);
        var info = new FileInfo(real.ToString());
        if (!info.Exists) return (_created, _created);
        return (info.LastWriteTimeUtc, info.LastWriteTimeUtc);
    }

    public VfsResult<VfsEntry> Lookup(ulong parent, string name)
    {
        lock (_gate)
        {
            if (!_byIno.TryGetValue(parent, out var directory)) return VfsResult<VfsEntry>.Failure(Errno.ENOENT);
            if (!directory.IsDirectory) return VfsResult<VfsEntry>.Failure(Errno.ENOTDIR);
            RecordSpelling(parent, name);
            // A missing name is a negative entry (inode 0), which the kernel may cache for NegativeTtl.
            return directory.Children!.TryGetValue(new VfsNameKey(name), out var child)
                ? VfsResult<VfsEntry>.Success(EntryFor(child))
                : VfsResult<VfsEntry>.Success(new VfsEntry(0, default, _policy.NegativeTtl, TimeSpan.Zero));
        }
    }

    // Callers hold the lock.
    private void RecordSpelling(ulong parent, string name)
    {
        var key = (parent, new VfsNameKey(name));
        if (!_spellings.TryGetValue(key, out var seen)) _spellings[key] = seen = new List<string>();
        if (seen.Contains(name, StringComparer.Ordinal)) return;
        seen.Add(name);
        if (seen.Count > MaxSpellingsPerName) seen.RemoveAt(0);
    }

    // The name the kernel used is always dropped, even if Lookup never saw it. Callers hold the lock.
    private void InvalidateName(ulong parent, string name)
    {
        if (Invalidation is not { } sink) return;
        RecordSpelling(parent, name);
        foreach (var spelling in _spellings[(parent, new VfsNameKey(name))].ToArray()) sink.InvalidateEntry(parent, spelling);
    }

    // Callers hold the lock.
    private void InvalidateInode(ulong ino) => Invalidation?.InvalidateInode(ino);

    // Inode numbers are kept for the life of the tree, so there is no per-lookup count to release.
    public void Forget(ulong ino, ulong lookups) { }

    public TimeSpan AttrTtl => _policy.AttrTtl;

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
        var writing = (flags & (VfsOpenFlags.Write | VfsOpenFlags.Truncate | VfsOpenFlags.Append)) != 0;
        if (writing && _staging is null) return VfsResult<VfsOpenFile>.Failure(Errno.EROFS);

        VfsNode node;
        lock (_gate)
        {
            if (!_byIno.TryGetValue(ino, out var found)) return VfsResult<VfsOpenFile>.Failure(Errno.ENOENT);
            if (found.IsDirectory) return VfsResult<VfsOpenFile>.Failure(Errno.EISDIR);
            node = found;
        }

        try
        {
            // Truncating throws the old bytes away, so there is nothing worth copying up.
            if (writing && EnsureStaged(node, copyContents: (flags & VfsOpenFlags.Truncate) == 0) is { } error)
                return VfsResult<VfsOpenFile>.Failure(error);

            VfsFileSource source;
            lock (_gate) source = node.Source!.Value;
            if (source.RealPath is not { } realPath) return VfsResult<VfsOpenFile>.Failure(Errno.EIO);

            // Only a staged file is ever opened for writing; the backing directory and the loose store are read-only here.
            var handle = File.OpenHandle(realPath.ToString(), FileMode.Open, writing ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if ((flags & VfsOpenFlags.Truncate) != 0) RandomAccess.SetLength(handle, 0);
            return VfsResult<VfsOpenFile>.Success(new VfsOpenFile
            {
                Ino = ino,
                Handle = handle,
                RealPath = realPath,
                Writable = writing,
                KeepCache = _policy.KeepCache,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult<VfsOpenFile>.Failure(ErrnoOf(ex)); }
    }

    private static Errno ErrnoOf(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => Errno.ENOENT,
        UnauthorizedAccessException => Errno.EACCES,
        _ => Errno.EIO,
    };

    public VfsResult<VfsReadReply> Read(VfsOpenFile file, ulong offset, int size)
    {
        if (size < 0) return VfsResult<VfsReadReply>.Failure(Errno.EINVAL);
        var length = (ulong)RandomAccess.GetLength(file.Handle);
        var available = offset >= length ? 0 : length - offset;
        return VfsResult<VfsReadReply>.Success(VfsReadReply.FromHandle(file.Handle, offset, (int)Math.Min((ulong)size, available)));
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

    public VfsResult<int> Write(VfsOpenFile file, ulong offset, ReadOnlySpan<byte> data)
    {
        if (!file.Writable) return VfsResult<int>.Failure(Errno.EACCES);
        Debug.Assert(_staging is not null && _staging.Contains(file.RealPath), "A write must only ever reach a file under the staging directory.");

        try
        {
            RandomAccess.Write(file.Handle, data, (long)offset);
            return VfsResult<int>.Success(data.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult<int>.Failure(ErrnoOf(ex)); }
    }

    public VfsResult<VfsAttr> SetAttr(ulong ino, VfsSetAttr change)
    {
        VfsNode node;
        lock (_gate)
        {
            if (!_byIno.TryGetValue(ino, out var found)) return VfsResult<VfsAttr>.Failure(Errno.ENOENT);
            node = found;
            var nothing = change is { Size: null, MTime: null, Mode: null };
            if (nothing) return VfsResult<VfsAttr>.Success(AttrFor(node));

            // Directories have no stored mode or times to change.
            if (node.IsDirectory) return VfsResult<VfsAttr>.Failure(change.Size is null ? Errno.ENOSYS : Errno.EISDIR);
        }

        if (_staging is null) return VfsResult<VfsAttr>.Failure(Errno.EROFS);

        try
        {
            if (EnsureStaged(node, copyContents: true) is { } error) return VfsResult<VfsAttr>.Failure(error);

            lock (_gate)
            {
                var real = node.Source!.Value.RealPath!.Value.ToString();
                if (change.Size is { } size)
                {
                    using var handle = File.OpenHandle(real, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    RandomAccess.SetLength(handle, (long)size);
                }
                if (change.Mode is { } mode) File.SetUnixFileMode(real, (UnixFileMode)(mode & 0xFFF));
                if (change.MTime is { } mtime) File.SetLastWriteTimeUtc(real, mtime.UtcDateTime);
                if (change.Size is not null) InvalidateInode(ino);
                return VfsResult<VfsAttr>.Success(AttrFor(node));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult<VfsAttr>.Failure(ErrnoOf(ex)); }
    }

    public VfsResult<(VfsEntry Entry, VfsOpenFile File)> Create(ulong parent, string name, uint mode, VfsOpenFlags flags)
    {
        if (_staging is null) return VfsResult<(VfsEntry, VfsOpenFile)>.Failure(Errno.EROFS);
        if (!IsValidName(name)) return VfsResult<(VfsEntry, VfsOpenFile)>.Failure(Errno.EINVAL);

        lock (_gate)
        {
            if (!_byIno.TryGetValue(parent, out var directory)) return VfsResult<(VfsEntry, VfsOpenFile)>.Failure(Errno.ENOENT);
            if (!directory.IsDirectory) return VfsResult<(VfsEntry, VfsOpenFile)>.Failure(Errno.ENOTDIR);
            var key = new VfsNameKey(name);
            if (directory.Children!.ContainsKey(key)) return VfsResult<(VfsEntry, VfsOpenFile)>.Failure(Errno.EEXIST);

            var relative = PathOf(directory, name);
            var real = _staging.PathFor(relative);
            try
            {
                real.Parent.CreateDirectory();
                var handle = File.OpenHandle(real.ToString(), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                File.SetUnixFileMode(real.ToString(), (UnixFileMode)(mode & 0xFFF));

                var file = new VfsNode
                {
                    Ino = _inodes.GetOrAdd(directory.Ino, key),
                    ParentIno = directory.Ino,
                    DisplayName = name,
                    IsDirectory = false,
                    Source = new VfsFileSource(relative, VfsSourceKind.Staged, real, Size.Zero, Hash.Zero, FromStaging: true),
                };
                directory.Children[key] = file;
                _byIno[file.Ino] = file;
                InvalidateName(directory.Ino, name);

                var open = new VfsOpenFile { Ino = file.Ino, Handle = handle, RealPath = real, Writable = true, KeepCache = _policy.KeepCache };
                return VfsResult<(VfsEntry, VfsOpenFile)>.Success((EntryFor(file), open));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult<(VfsEntry, VfsOpenFile)>.Failure(ErrnoOf(ex)); }
        }
    }

    // The mode is not kept: a directory's attributes are the same for every directory the tree serves.
    public VfsResult<VfsEntry> MkDir(ulong parent, string name, uint mode)
    {
        if (_staging is null) return VfsResult<VfsEntry>.Failure(Errno.EROFS);
        if (!IsValidName(name)) return VfsResult<VfsEntry>.Failure(Errno.EINVAL);

        lock (_gate)
        {
            if (!_byIno.TryGetValue(parent, out var directory)) return VfsResult<VfsEntry>.Failure(Errno.ENOENT);
            if (!directory.IsDirectory) return VfsResult<VfsEntry>.Failure(Errno.ENOTDIR);
            var key = new VfsNameKey(name);
            if (directory.Children!.ContainsKey(key)) return VfsResult<VfsEntry>.Failure(Errno.EEXIST);

            try { _staging.PathFor(PathOf(directory, name)).CreateDirectory(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult<VfsEntry>.Failure(ErrnoOf(ex)); }

            var created = NewDirectory(_inodes.GetOrAdd(directory.Ino, key), directory.Ino, name, lower: false);
            directory.Children[key] = created;
            _byIno[created.Ino] = created;
            InvalidateName(directory.Ino, name);
            return VfsResult<VfsEntry>.Success(EntryFor(created));
        }
    }

    public VfsResult Unlink(ulong parent, string name)
    {
        if (_staging is null) return VfsResult.Failure(Errno.EROFS);

        lock (_gate)
        {
            if (!TryGetChild(parent, name, out var directory, out var node, out var error)) return VfsResult.Failure(error);
            if (node.IsDirectory) return VfsResult.Failure(Errno.EISDIR);

            var source = node.Source!.Value;
            var relative = PathOf(node);
            try
            {
                // The name must stay gone after a remount, so the whiteout is written before anything is deleted.
                if (!source.FromStaging || node.Shadows) AddWhiteout(relative);
                if (source.FromStaging) File.Delete(source.RealPath!.Value.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult.Failure(ErrnoOf(ex)); }

            Detach(directory, node);
            InvalidateName(directory.Ino, name);
            return VfsResult.Success;
        }
    }

    public VfsResult RmDir(ulong parent, string name)
    {
        if (_staging is null) return VfsResult.Failure(Errno.EROFS);

        lock (_gate)
        {
            if (!TryGetChild(parent, name, out var directory, out var node, out var error)) return VfsResult.Failure(error);
            if (!node.IsDirectory) return VfsResult.Failure(Errno.ENOTDIR);
            if (node.Children!.Count > 0) return VfsResult.Failure(Errno.ENOTEMPTY);

            var relative = PathOf(node);
            try
            {
                if (node.Lower) AddWhiteout(relative);
                var staged = _staging.PathFor(relative);
                if (staged.DirectoryExists()) Directory.Delete(staged.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult.Failure(ErrnoOf(ex)); }

            Detach(directory, node);
            InvalidateName(directory.Ino, name);
            return VfsResult.Success;
        }
    }

    public VfsResult Rename(ulong parent, string name, ulong newParent, string newName)
    {
        if (_staging is null) return VfsResult.Failure(Errno.EROFS);
        if (!IsValidName(newName)) return VfsResult.Failure(Errno.EINVAL);

        // A file from below staging is copied up under its old name first, which takes the long copy
        // out of the lock; what is left to do is the same plain rename a staged file gets.
        VfsNode? toCopy = null;
        lock (_gate)
        {
            if (TryGetChild(parent, name, out _, out var found, out _) && !found.IsDirectory && !found.Source!.Value.FromStaging) toCopy = found;
        }
        if (toCopy is not null)
        {
            try { if (EnsureStaged(toCopy, copyContents: true) is { } copyError) return VfsResult.Failure(copyError); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult.Failure(ErrnoOf(ex)); }
        }

        lock (_gate)
        {
            if (!TryGetChild(parent, name, out var fromDirectory, out var node, out var error)) return VfsResult.Failure(error);
            if (!_byIno.TryGetValue(newParent, out var toDirectory)) return VfsResult.Failure(Errno.ENOENT);
            if (!toDirectory.IsDirectory) return VfsResult.Failure(Errno.ENOTDIR);

            var fromKey = new VfsNameKey(node.DisplayName);
            var toKey = new VfsNameKey(newName);
            toDirectory.Children!.TryGetValue(toKey, out var target);
            if (target == node) target = null;

            if (node.IsDirectory)
            {
                // Files below staging cannot move between directories in place and the error that tells a tool to copy instead is EXDEV.
                if (!IsStagedOnly(node)) return VfsResult.Failure(Errno.EXDEV);
                for (var up = toDirectory; ; up = _byIno[up.ParentIno])
                {
                    if (up == node) return VfsResult.Failure(Errno.EINVAL);
                    if (up.Ino == IVfsOperations.RootInode) break;
                }
                if (target is not null && !target.IsDirectory) return VfsResult.Failure(Errno.ENOTDIR);
                if (target is not null && target.Children!.Count > 0) return VfsResult.Failure(Errno.ENOTEMPTY);
            }
            else if (target is { IsDirectory: true })
            {
                return VfsResult.Failure(Errno.EISDIR);
            }

            var oldPath = PathOf(node);
            var newPath = PathOf(toDirectory, newName);
            if (string.Equals(oldPath.ToString(), newPath.ToString(), StringComparison.Ordinal)) return VfsResult.Success;

            // A staged file knows where it is on disk. A staged directory is looked for at its display
            // path, which is not where it is when staging spells a parent differently from the tree
            // (staged data/ under a backing Data/); moving it in place would leave its files behind,
            // so that is refused like a directory with files from below.
            var from = node.IsDirectory ? _staging.PathFor(oldPath) : node.Source!.Value.RealPath!.Value;
            if (node.IsDirectory && !from.DirectoryExists()) return VfsResult.Failure(Errno.EXDEV);
            var to = _staging.PathFor(newPath);
            var needsWhiteout = !node.IsDirectory && node.Shadows;
            var shadowsAfter = target is not null && (target.IsDirectory || !target.Source!.Value.FromStaging || target.Shadows);
            try
            {
                // Written first, as Unlink does: staged files go over whiteouts when the tree is rebuilt,
                // so if the move then fails the old name still shows its staged copy.
                if (needsWhiteout) AddWhiteout(oldPath);
                to.Parent.CreateDirectory();
                if (node.IsDirectory)
                {
                    if (to.DirectoryExists()) Directory.Delete(to.ToString());
                    Directory.Move(from.ToString(), to.ToString());
                }
                else
                {
                    File.Move(from.ToString(), to.ToString(), overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return VfsResult.Failure(ErrnoOf(ex)); }

            if (target is not null)
            {
                Unindex(target);
                toDirectory.Children.Remove(toKey);
                // The emptied lower directory is still there underneath, so removing this one later must whiteout it.
                if (target.IsDirectory) node.Lower = target.Lower;
            }
            fromDirectory.Children!.Remove(fromKey);
            var moved = _inodes.Move(fromDirectory.Ino, fromKey, toDirectory.Ino, toKey);
            Debug.Assert(moved, "Every node in the tree has an inode number under its parent and name.");
            node.ParentIno = toDirectory.Ino;
            node.DisplayName = newName;
            toDirectory.Children[toKey] = node;

            if (node.IsDirectory) Relocate(node);
            else
            {
                node.Shadows = shadowsAfter;
                node.Source = node.Source!.Value with { Path = newPath, RealPath = to };
            }
            InvalidateName(fromDirectory.Ino, name);
            InvalidateName(toDirectory.Ino, newName);
            return VfsResult.Success;
        }
    }

    private static bool IsValidName(string name) => name.Length > 0 && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\0');

    // Callers hold the lock.
    private bool TryGetChild(ulong parent, string name, out VfsNode directory, out VfsNode node, out Errno error)
    {
        node = null!;
        if (!_byIno.TryGetValue(parent, out directory!)) { error = Errno.ENOENT; return false; }
        if (!directory.IsDirectory) { error = Errno.ENOTDIR; return false; }
        if (!directory.Children!.TryGetValue(new VfsNameKey(name), out var child)) { error = Errno.ENOENT; return false; }
        node = child;
        error = default;
        return true;
    }

    private void Detach(VfsNode directory, VfsNode node)
    {
        directory.Children!.Remove(new VfsNameKey(node.DisplayName));
        Unindex(node);
    }

    private RelativePath PathOf(VfsNode node)
    {
        var names = new List<string>();
        for (var current = node; current.Ino != IVfsOperations.RootInode; current = _byIno[current.ParentIno]) names.Add(current.DisplayName);
        names.Reverse();
        return RelativePath.FromUnsanitizedInput(string.Join('/', names));
    }

    private RelativePath PathOf(VfsNode directory, string name)
    {
        var parent = PathOf(directory).ToString();
        return RelativePath.FromUnsanitizedInput(parent.Length == 0 ? name : $"{parent}/{name}");
    }

    // A directory can be moved by renaming its staging directory only when everything under it lives there.
    private static bool IsStagedOnly(VfsNode directory)
    {
        if (directory.Lower) return false;
        return directory.Children!.Values.All(child => child.IsDirectory
            ? IsStagedOnly(child)
            : child.Source!.Value.FromStaging && !child.Shadows);
    }

    // After a directory moved, the files below it are somewhere else on disk.
    private void Relocate(VfsNode directory)
    {
        foreach (var child in directory.Children!.Values)
        {
            if (child.IsDirectory) { Relocate(child); continue; }
            var path = PathOf(child);
            child.Source = child.Source!.Value with { Path = path, RealPath = _staging!.PathFor(path) };
        }
    }

    // Callers hold the lock.
    private void AddWhiteout(RelativePath path)
    {
        if (_whiteouts.ContainsKey(path.ToString())) return;
        _whiteouts[path.ToString()] = path;
        try { _staging!.WriteWhiteouts(_whiteouts.Values); }
        catch
        {
            _whiteouts.Remove(path.ToString());
            throw;
        }
    }

    // Puts the file's bytes under the staging directory, unless they already are, so a write has
    // somewhere to land that is not the backing directory or the loose store. Returns the error to
    // report, or null once the node's source is a staging file.
    private Errno? EnsureStaged(VfsNode node, bool copyContents)
    {
        if (_staging is null) return Errno.EROFS;

        lock (node.CowLock)
        {
            VfsFileSource source;
            AbsolutePath destination;
            lock (_gate)
            {
                if (!_byIno.TryGetValue(node.Ino, out var live) || live != node) return Errno.ENOENT;
                source = node.Source!.Value;
                if (source.FromStaging) return null;
                if (source.RealPath is null) return Errno.EIO;
                destination = _staging.PathFor(PathOf(node));
            }

            // Built beside the staging directory and renamed in, so a scan never sees half a copy.
            var temp = _staging.NewTempPath();
            try
            {
                if (copyContents) File.Copy(source.RealPath.Value.ToString(), temp.ToString());
                else File.WriteAllBytes(temp.ToString(), []);
                // Whatever the original's mode, the copy must be one the daemon can open for writing.
                File.SetUnixFileMode(temp.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                destination.Parent.CreateDirectory();

                lock (_gate)
                {
                    // Unlinked while it was being copied.
                    if (!_byIno.TryGetValue(node.Ino, out var live) || live != node) return Errno.ENOENT;
                    File.Move(temp.ToString(), destination.ToString());
                    node.Source = source with { Kind = VfsSourceKind.Staged, RealPath = destination, FromStaging = true };
                    node.Backing = null;
                    node.Shadows = true;
                    // Same inode number, different file: size, times and link count under the attribute TTL are stale.
                    InvalidateInode(node.Ino);
                }
                return null;
            }
            finally
            {
                if (temp.FileExists) temp.Delete();
            }
        }
    }
}
