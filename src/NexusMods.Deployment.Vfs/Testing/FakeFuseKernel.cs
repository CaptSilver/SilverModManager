using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs.Testing;

/// <summary>
/// An <see cref="IFuseKernel"/> with no kernel: it keeps the operations it is given and offers a
/// path API that talks to them the way the kernel would, one Lookup per path segment. Every Lookup
/// is recorded in <see cref="Lookups"/> so tests can assert on how often the tree is asked.
/// </summary>
public sealed class FakeFuseKernel : IFuseKernel
{
    private readonly object _gate = new();
    private readonly List<(ulong Parent, string Name)> _lookups = new();
    private readonly List<(string Kind, ulong Node, string? Name)> _invalidations = new();
    private IVfsOperations? _operations;
    private VfsPathWalker? _walker;

    public IVfsOperations Operations => _operations ?? throw new InvalidOperationException("Nothing is mounted.");

    private VfsPathWalker Walker => _walker ?? throw new InvalidOperationException("Nothing is mounted.");

    public IReadOnlyList<(ulong Parent, string Name)> Lookups
    {
        get { lock (_gate) return _lookups.ToArray(); }
    }

    /// <summary>
    /// What the tree asked the kernel to drop, in order: <c>("Inode", ino, null)</c> or
    /// <c>("Entry", parent, name)</c>.
    /// </summary>
    public IReadOnlyList<(string Kind, ulong Node, string? Name)> Invalidations
    {
        get { lock (_gate) return _invalidations.ToArray(); }
    }

    public Task<IFuseMount> Mount(IVfsOperations operations, FuseMountOptions options, CancellationToken token)
    {
        if (_operations is not null) throw new InvalidOperationException("The fake kernel serves one mount.");
        _operations = operations;
        _walker = new VfsPathWalker(operations, (parent, name) =>
        {
            lock (_gate) _lookups.Add((parent, name));
        });
        var mount = new FakeFuseMount(this, options.MountPoint, token);
        if (operations is VfsTree tree) tree.Invalidation = mount;
        return Task.FromResult<IFuseMount>(mount);
    }

    public VfsResult<VfsEntry> Resolve(string path) => Walker.Walk(path);

    public VfsResult<IReadOnlyList<VfsDirEntry>> List(string path)
    {
        var directory = Resolve(path);
        if (!directory.Ok) return VfsResult<IReadOnlyList<VfsDirEntry>>.Failure(directory.Error!.Value);

        var all = new List<VfsDirEntry>();
        ulong offset = 0;
        while (true)
        {
            var page = Operations.ReadDirPlus(directory.Value.Ino, offset, 64);
            if (!page.Ok) return VfsResult<IReadOnlyList<VfsDirEntry>>.Failure(page.Error!.Value);
            if (page.Value.Entries.Count == 0) break;
            all.AddRange(page.Value.Entries);
            offset = page.Value.Entries[^1].NextOffset;
        }
        return VfsResult<IReadOnlyList<VfsDirEntry>>.Success(all);
    }

    public VfsResult<VfsOpenFile> Open(string path, VfsOpenFlags flags)
    {
        var entry = Resolve(path);
        return entry.Ok ? Operations.Open(entry.Value.Ino, flags) : VfsResult<VfsOpenFile>.Failure(entry.Error!.Value);
    }

    public VfsResult<byte[]> Read(VfsOpenFile file, ulong offset, int size)
    {
        var reply = Operations.Read(file, offset, size);
        if (!reply.Ok) return VfsResult<byte[]>.Failure(reply.Error!.Value);

        var buffer = new byte[reply.Value.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(reply.Value.Handle, buffer.AsSpan(read), (long)reply.Value.Offset + read);
            if (n == 0) break;
            read += n;
        }
        return VfsResult<byte[]>.Success(buffer[..read]);
    }

    public VfsResult<int> Write(VfsOpenFile file, ulong offset, byte[] data) => Operations.Write(file, offset, data);

    public VfsResult Release(VfsOpenFile file) => Operations.Release(file);

    public VfsResult<(VfsEntry Entry, VfsOpenFile File)> Create(string path, uint mode, VfsOpenFlags flags)
    {
        var parent = Walker.WalkParent(path);
        return parent.Ok
            ? Operations.Create(parent.Value.Parent, parent.Value.Name, mode, flags)
            : VfsResult<(VfsEntry, VfsOpenFile)>.Failure(parent.Error!.Value);
    }

    public VfsResult<VfsEntry> MkDir(string path, uint mode)
    {
        var parent = Walker.WalkParent(path);
        return parent.Ok
            ? Operations.MkDir(parent.Value.Parent, parent.Value.Name, mode)
            : VfsResult<VfsEntry>.Failure(parent.Error!.Value);
    }

    /// <summary>
    /// Removes a file or an empty directory, choosing Unlink or RmDir by what the path resolves to.
    /// </summary>
    public VfsResult Delete(string path)
    {
        var entry = Resolve(path);
        if (!entry.Ok) return VfsResult.Failure(entry.Error!.Value);
        var parent = Walker.WalkParent(path);
        if (!parent.Ok) return VfsResult.Failure(parent.Error!.Value);

        var isDirectory = (entry.Value.Attr.Mode & 0xF000) == 0x4000;
        return isDirectory
            ? Operations.RmDir(parent.Value.Parent, parent.Value.Name)
            : Operations.Unlink(parent.Value.Parent, parent.Value.Name);
    }

    public VfsResult Rename(string from, string to)
    {
        var source = Walker.WalkParent(from);
        if (!source.Ok) return VfsResult.Failure(source.Error!.Value);
        var destination = Walker.WalkParent(to);
        if (!destination.Ok) return VfsResult.Failure(destination.Error!.Value);

        return Operations.Rename(source.Value.Parent, source.Value.Name, destination.Value.Parent, destination.Value.Name);
    }

    private void Record(string kind, ulong node, string? name)
    {
        lock (_gate) _invalidations.Add((kind, node, name));
    }

    private sealed class FakeFuseMount : IFuseMount, IVfsInvalidationSink
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FakeFuseKernel _kernel;

        public FakeFuseMount(FakeFuseKernel kernel, AbsolutePath mountPoint, CancellationToken token)
        {
            _kernel = kernel;
            MountPoint = mountPoint;
            token.Register(Finish);
        }

        private void Finish()
        {
            if (_kernel._operations is VfsTree tree && ReferenceEquals(tree.Invalidation, this)) tree.Invalidation = null;
            _completion.TrySetResult();
        }

        public AbsolutePath MountPoint { get; }
        public Task Completion => _completion.Task;

        // There is no kernel cache to drop, so the request is only written down.
        public void InvalidateEntry(ulong parent, string name) => _kernel.Record("Entry", parent, name);
        public void InvalidateInode(ulong ino) => _kernel.Record("Inode", ino, null);

        public Task Unmount(CancellationToken token)
        {
            Finish();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Finish();
            return ValueTask.CompletedTask;
        }
    }
}
