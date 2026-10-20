using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NexusMods.Deployment.Fuse.Mount;
using NexusMods.Deployment.Fuse.Native;
using NexusMods.Deployment.Vfs;
using NexusMods.Paths;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Fuse.Kernel;

/// <summary>What the library and the kernel agreed on during <c>init</c>, for diagnostics.</summary>
internal sealed record FuseSessionInfo(string Soname, int FuseVersion, uint ProtoMajor, uint ProtoMinor, uint MaxWrite, bool PassthroughCapable);

/// <summary>
/// One libfuse session. The session loop runs on its own thread for the life of the mount, and libfuse calls
/// the <c>[UnmanagedCallersOnly]</c> entry points below from its worker threads. Every entry point catches: an
/// exception unwinding into libfuse would take the process down, so it is logged and the request answered EIO.
/// </summary>
internal sealed class LibFuseLowLevelMount : IFuseMount, IVfsInvalidationSink
{
    private const int MaxWrite = 1 << 20;
    private const int EIO = 5;
    private const int ENOENT = 2;
    private const int EBADF = 9;
    private const int ENOTCONN = 107;
    private const int ENODEV = 19;
    private const int EPERM = 1;
    private const int EINVAL = 22;

    // The smallest a readdirplus record can be: a 128 byte fuse_entry_out and a 24 byte dirent header.
    private const int MinDirEntryPlusSize = 152;

    private const int OAccessMode = 3;
    private const int OTruncate = 0x200;
    private const int OAppend = 0x400;

    private const int SetAttrMode = 1 << 0;
    private const int SetAttrUid = 1 << 1;
    private const int SetAttrGid = 1 << 2;
    private const int SetAttrSize = 1 << 3;
    private const int SetAttrMTime = 1 << 5;
    private const int SetAttrMTimeNow = 1 << 8;

    private const uint BlockSize = 4096;

    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnmountTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BusyRetryWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BusyRetryInterval = TimeSpan.FromMilliseconds(50);
    private static readonly uint Uid = LibcProcess.geteuid();
    private static readonly uint Gid = LibcProcess.getegid();

    private readonly IVfsOperations _operations;
    private readonly FuseMountOptions _options;
    private readonly ILogger _logger;
    private readonly IFuseUnmounter _unmounter;
    private readonly string _soname;
    private readonly int _fuseVersion;
    private readonly string _realMountPoint;
    private readonly string _fileSystemType;

    private readonly ConcurrentDictionary<ulong, VfsOpenFile> _files = new();
    private long _lastHandle;

    // Kernel notifications are written from one thread that is never a request thread: the kernel holds the
    // parent's lock while a request runs, and a notify issued from inside one would wait on that lock forever.
    private readonly Channel<Invalidation> _invalidations = Channel.CreateUnbounded<Invalidation>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sessionGate = new();
    private readonly Lazy<Task> _disposal;

    private GCHandle _self;
    private IntPtr _session;
    private Thread? _loopThread;
    private Thread? _invalidationThread;
    private volatile bool _unmounting;
    private int _exitCode;
    private volatile FuseSessionInfo? _negotiated;

    internal LibFuseLowLevelMount(
        IVfsOperations operations, FuseMountOptions options, ILogger logger, IFuseUnmounter unmounter,
        string soname, int fuseVersion, string realMountPoint)
    {
        _operations = operations;
        _options = options;
        _logger = logger;
        _unmounter = unmounter;
        _soname = soname;
        _fuseVersion = fuseVersion;
        _realMountPoint = realMountPoint;
        _fileSystemType = FuseMountArguments.FileSystemType(options);
        _disposal = new Lazy<Task>(DisposeCore);
        MountPoint = options.MountPoint;
    }

    private readonly record struct Invalidation(ulong Node, string? Name);

    public AbsolutePath MountPoint { get; }

    public Task Completion => _completion.Task;

    /// <summary>What <c>init</c> saw. Null until the kernel has sent it.</summary>
    internal FuseSessionInfo? Negotiated => _negotiated;

    /// <summary>
    /// The number a failed operation is answered with. <c>fuse_reply_err</c> takes the positive errno, and
    /// <see cref="Errno"/> carries the Linux numbers, so this is the identity: a cross-folder rename must reach
    /// the caller as EXDEV, not as whatever a lossy translation would pick.
    /// </summary>
    internal static int ReplyCode(Errno errno) => (int)errno;

    // ---- lifecycle -------------------------------------------------------------------------------------

    internal async Task Start()
    {
        try
        {
            Open();
            await WaitUntilMounted().ConfigureAwait(false);
        }
        catch
        {
            await AbortStart().ConfigureAwait(false);
            throw;
        }
    }

    private unsafe void Open()
    {
        _self = GCHandle.Alloc(this);

        // argv[0] is a program name: libfuse treats an option in argv[0] as ignored.
        var arguments = new List<string> { "silvermod" };
        arguments.AddRange(FuseMountArguments.SessionArguments(_options));
        var argv = (IntPtr*)NativeMemory.AllocZeroed((nuint)(arguments.Count + 1), (nuint)sizeof(IntPtr));
        try
        {
            for (var i = 0; i < arguments.Count; i++) argv[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            var args = new FuseArgs { Argc = arguments.Count, Argv = (IntPtr)argv, Allocated = 0 };
            var ops = BuildOperations();
            var version = LibfuseVersion.Current;

            _session = LibFuse3LowLevel.fuse_session_new(&args, &ops, (nuint)sizeof(FuseLowlevelOps), &version, GCHandle.ToIntPtr(_self));
            // Parsing replaces the argument vector with a copy libfuse owns; ours stays ours to free.
            LibFuse3LowLevel.fuse_opt_free_args(&args);
        }
        finally
        {
            for (var i = 0; i < arguments.Count; i++) Marshal.FreeCoTaskMem(argv[i]);
            NativeMemory.Free(argv);
        }

        if (_session == IntPtr.Zero)
        {
            _self.Free();
            throw new IOException("fuse_session_new failed; libfuse reports the reason on stderr.");
        }

        var rc = LibFuse3LowLevel.fuse_session_mount(_session, _realMountPoint);
        if (rc != 0)
        {
            LibFuse3LowLevel.fuse_session_destroy(_session);
            _session = IntPtr.Zero;
            _self.Free();
            throw new IOException($"fuse_session_mount of `{_realMountPoint}` failed with {rc}; libfuse reports the reason on stderr.");
        }

        if (_operations is VfsTree tree) tree.Invalidation = this;

        _invalidationThread = new Thread(DrainInvalidations) { Name = $"fuse-inval:{MountPoint.FileName}", IsBackground = true };
        _invalidationThread.Start();
        _loopThread = new Thread(RunSession) { Name = $"fuse:{MountPoint.FileName}", IsBackground = true };
        _loopThread.Start();
    }

    private async Task WaitUntilMounted()
    {
        var deadline = DateTime.UtcNow + MountTimeout;
        while (!MountTable.IsMounted(_realMountPoint, _fileSystemType))
        {
            if (_completion.Task.IsCompleted)
                throw new IOException($"The FUSE session for `{_realMountPoint}` ended before the mount appeared in {MountTable.Path} (session loop returned {_exitCode}).");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"`{_realMountPoint}` did not appear as {_fileSystemType} in {MountTable.Path} within {MountTimeout.TotalSeconds:0} s.");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    // The mount never became usable. Take the session down so nothing is left mounted or running.
    private async Task AbortStart()
    {
        try
        {
            if (_loopThread is null) return;
            await _unmounter.Unmount(MountPoint, lazy: true, CancellationToken.None).ConfigureAwait(false);
            ExitSession();
            await _completion.Task.WaitAsync(MountTimeout).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cleaning up the failed mount of `{MountPoint}` did not finish", MountPoint);
        }
    }

    private void RunSession()
    {
        var rc = -1;
        try
        {
            var config = LibFuse3LowLevel.fuse_loop_cfg_create();
            try
            {
                LibFuse3LowLevel.fuse_loop_cfg_set_clone_fd(config, 1);
                if (_options.Workers > 0) LibFuse3LowLevel.fuse_loop_cfg_set_max_threads(config, (uint)_options.Workers);
                rc = LibFuse3LowLevel.fuse_session_loop_mt(_session, config);
            }
            finally
            {
                LibFuse3LowLevel.fuse_loop_cfg_destroy(config);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The FUSE session loop for `{MountPoint}` failed", MountPoint);
        }
        finally
        {
            Teardown(rc);
        }
    }

    // Runs on the loop thread once the loop has returned, so nothing is serving requests any more.
    private void Teardown(int loopResult)
    {
        _exitCode = loopResult;
        _unmounting = true;
        if (_operations is VfsTree tree && ReferenceEquals(tree.Invalidation, this)) tree.Invalidation = null;

        // No notify may run against a destroyed session, so the queue is closed and drained first.
        _invalidations.Writer.TryComplete();
        var drained = _invalidationThread?.Join(UnmountTimeout) ?? true;

        if (drained)
        {
            lock (_sessionGate)
            {
                LibFuse3LowLevel.fuse_session_unmount(_session);
                LibFuse3LowLevel.fuse_session_destroy(_session);
                _session = IntPtr.Zero;
            }
            _self.Free();
        }
        else
        {
            // Destroying the session under a thread still inside a notify would crash; a leak is the lesser harm.
            _logger.LogError("The invalidation thread for `{MountPoint}` did not stop; leaking the FUSE session", MountPoint);
        }

        _completion.TrySetResult();
    }

    private void ExitSession()
    {
        lock (_sessionGate)
        {
            if (_session != IntPtr.Zero) LibFuse3LowLevel.fuse_session_exit(_session);
        }
    }

    public async Task Unmount(CancellationToken token)
    {
        _unmounting = true;
        if (!_completion.Task.IsCompleted)
        {
            // fusermount3 detaches the mount, which makes the workers' reads fail and the loop return. The session is
            // not exited first: a refused unmount must leave it serving, and fusermount3 itself needs it, because its
            // exec closes the inherited descriptors of files open on this mount and each close waits for a FLUSH reply.
            // The kernel releases a file it was just told to close asynchronously, so the mount can still look busy
            // for a moment after the last close. Only a mount that stays busy is a real failure.
            var retryUntil = DateTime.UtcNow + BusyRetryWindow;
            var result = await _unmounter.Unmount(MountPoint, lazy: false, token).ConfigureAwait(false);
            while (result.Status == UnmountStatus.Failed && DateTime.UtcNow < retryUntil)
            {
                await Task.Delay(BusyRetryInterval, token).ConfigureAwait(false);
                result = await _unmounter.Unmount(MountPoint, lazy: false, token).ConfigureAwait(false);
            }
            if (result.Status == UnmountStatus.Failed)
            {
                // Still mounted and serving, so notify failures are real errors again.
                _unmounting = false;
                throw new IOException($"fusermount3 could not unmount `{MountPoint}`: {result.StdErr.Trim()}");
            }
        }
        await _completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(_disposal.Value);

    private async Task DisposeCore()
    {
        if (_completion.Task.IsCompleted) return;

        try
        {
            using var timeout = new CancellationTokenSource(UnmountTimeout);
            await Unmount(timeout.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Unmounting `{MountPoint}` did not finish; detaching it lazily", MountPoint);
        }

        // A lazy unmount removes the mount even with files open. The session then ends once the last of those
        // files is closed, and Completion reports it; waiting for that here would hang on a file we do not own.
        using var lazyTimeout = new CancellationTokenSource(UnmountTimeout);
        var lazy = await _unmounter.Unmount(MountPoint, lazy: true, lazyTimeout.Token).ConfigureAwait(false);
        if (lazy.Status == UnmountStatus.Failed)
            _logger.LogError("Lazy unmount of `{MountPoint}` failed: {StdErr}", MountPoint, lazy.StdErr.Trim());
    }

    // ---- invalidation ------------------------------------------------------------------------------------

    public void InvalidateInode(ulong ino) => _invalidations.Writer.TryWrite(new Invalidation(ino, null));

    public void InvalidateEntry(ulong parent, string name) => _invalidations.Writer.TryWrite(new Invalidation(parent, name));

    private void DrainInvalidations()
    {
        var reader = _invalidations.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var item))
            {
                try { Notify(item); }
                catch (Exception e) { _logger.LogError(e, "Dropping a kernel cache entry failed"); }
            }
        }
    }

    private unsafe void Notify(Invalidation item)
    {
        int rc;
        if (item.Name is null)
        {
            rc = LibFuse3LowLevel.fuse_lowlevel_notify_inval_inode(_session, item.Node, 0, 0);
        }
        else
        {
            // libfuse copies namelen + 1 bytes, so the terminator has to be there; the length excludes it.
            var name = Utf8Z(item.Name);
            fixed (byte* bytes = name)
                rc = LibFuse3LowLevel.fuse_lowlevel_notify_inval_entry(_session, item.Node, bytes, (nuint)(name.Length - 1));
        }

        if (rc == 0) return;
        var errno = -rc;
        // ENOENT: the kernel never cached that name. The others: the mount is already going away.
        if (errno == ENOENT || (_unmounting && errno is EBADF or ENOTCONN or ENODEV)) return;
        _logger.LogWarning("Kernel cache invalidation for {Node} {Name} failed with errno {Errno}", item.Node, item.Name, errno);
    }

    // ---- request plumbing --------------------------------------------------------------------------------

    private static LibFuseLowLevelMount Of(IntPtr req) =>
        (LibFuseLowLevelMount)GCHandle.FromIntPtr(LibFuse3LowLevel.fuse_req_userdata(req)).Target!;

    private static unsafe string Name(byte* name) => Marshal.PtrToStringUTF8((IntPtr)name)!;

    private static byte[] Utf8Z(string text)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(text) + 1];
        System.Text.Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    private static void ReplyError(IntPtr req, Errno errno) => LibFuse3LowLevel.fuse_reply_err(req, ReplyCode(errno));

    private void Fail(IntPtr req, Exception e)
    {
        _logger.LogError(e, "A FUSE request on `{MountPoint}` threw", MountPoint);
        LibFuse3LowLevel.fuse_reply_err(req, EIO);
    }

    private static void SplitTime(DateTimeOffset time, out long seconds, out long nanoseconds)
    {
        var ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var rest);
        if (rest < 0)
        {
            seconds--;
            rest += TimeSpan.TicksPerSecond;
        }
        nanoseconds = rest * 100;
    }

    private static LibcStat.Buffer StatOf(in VfsAttr attr)
    {
        SplitTime(attr.MTime, out var modifySeconds, out var modifyNanoseconds);
        SplitTime(attr.CTime, out var changeSeconds, out var changeNanoseconds);
        return new LibcStat.Buffer
        {
            Inode = attr.Ino,
            Mode = attr.Mode,
            NLink = attr.NLink,
            // default_permissions makes the kernel check these against the caller, so they have to be the
            // mounting user's; root-owned files would refuse every write.
            Uid = Uid,
            Gid = Gid,
            Size = (long)attr.Size,
            BlockSize = BlockSize,
            Blocks = (long)((attr.Size + 511) / 512),
            AccessSeconds = modifySeconds,
            AccessNanoseconds = modifyNanoseconds,
            ModifySeconds = modifySeconds,
            ModifyNanoseconds = modifyNanoseconds,
            ChangeSeconds = changeSeconds,
            ChangeNanoseconds = changeNanoseconds,
        };
    }

    // A negative entry (inode 0) carries only the timeout the kernel may cache the absence for.
    private static FuseEntryParam EntryOf(in VfsEntry entry) => new()
    {
        Ino = entry.Ino,
        Generation = 0,
        Attr = entry.Ino == 0 ? default : StatOf(entry.Attr),
        AttrTimeout = entry.AttrTtl.TotalSeconds,
        EntryTimeout = entry.EntryTtl.TotalSeconds,
    };

    private static VfsOpenFlags OpenFlagsOf(int flags)
    {
        var result = (flags & OAccessMode) switch
        {
            1 => VfsOpenFlags.Write,
            2 => VfsOpenFlags.Read | VfsOpenFlags.Write,
            _ => VfsOpenFlags.Read,
        };
        if ((flags & OTruncate) != 0) result |= VfsOpenFlags.Truncate;
        if ((flags & OAppend) != 0) result |= VfsOpenFlags.Append;
        return result;
    }

    private unsafe ulong Register(VfsOpenFile file, FuseFileInfo* info)
    {
        var handle = (ulong)Interlocked.Increment(ref _lastHandle);
        _files[handle] = file;
        info->Fh = handle;
        info->KeepCache = file.KeepCache;
        info->DirectIo = file.DirectIo;
        return handle;
    }

    private void Unregister(ulong handle)
    {
        if (_files.TryRemove(handle, out var file)) _operations.Release(file);
    }

    // ---- operations --------------------------------------------------------------------------------------

    private unsafe void DoLookup(IntPtr req, ulong parent, string name)
    {
        var result = _operations.Lookup(parent, name);
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }
        var entry = EntryOf(result.Value);
        LibFuse3LowLevel.fuse_reply_entry(req, &entry);
    }

    private void DoGetAttr(IntPtr req, ulong ino)
    {
        var result = _operations.GetAttr(ino);
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }
        ReplyAttr(req, result.Value);
    }

    private unsafe void ReplyAttr(IntPtr req, in VfsAttr attr)
    {
        var stat = StatOf(attr);
        LibFuse3LowLevel.fuse_reply_attr(req, &stat, _operations.AttrTtl.TotalSeconds);
    }

    private unsafe void DoSetAttr(IntPtr req, ulong ino, LibcStat.Buffer* attr, int toSet)
    {
        if ((toSet & (SetAttrUid | SetAttrGid)) != 0) { ReplyError(req, Errno.EPERM); return; }

        ulong? size = (toSet & SetAttrSize) != 0 ? (ulong)attr->Size : null;
        uint? mode = (toSet & SetAttrMode) != 0 ? attr->Mode : null;
        DateTimeOffset? mtime = (toSet & SetAttrMTimeNow) != 0 ? DateTimeOffset.UtcNow : (toSet & SetAttrMTime) != 0 ? attr->MTime : null;

        var result = _operations.SetAttr(ino, new VfsSetAttr(size, mtime, mode));
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }
        ReplyAttr(req, result.Value);
    }

    private unsafe void DoReadDirPlus(IntPtr req, ulong ino, nuint requested, long offset)
    {
        var capacity = (int)Math.Min(requested, 1u << 20);
        var rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            fixed (byte* buffer = rented)
            {
                var used = 0;
                var full = false;

                // "." and ".." are synthesised at offsets 1 and 2; the tree's entries follow, shifted by two.
                // Both carry inode 0 so the kernel takes no reference on them; d_ino comes from attr.
                if (offset < 1) full = !AddDot(req, buffer, ref used, capacity, ".", ino, 1);
                if (!full && offset < 2) full = !AddDot(req, buffer, ref used, capacity, "..", ino, 2);

                if (!full)
                {
                    var treeOffset = offset >= 2 ? (ulong)(offset - 2) : 0;
                    var page = _operations.ReadDirPlus(ino, treeOffset, Math.Max(1, (capacity - used) / MinDirEntryPlusSize));
                    if (!page.Ok) { ReplyError(req, page.Error!.Value); return; }

                    foreach (var item in page.Value.Entries)
                    {
                        var entry = EntryOf(item.Entry);
                        if (!Add(req, buffer, ref used, capacity, item.Name, entry, (long)item.NextOffset + 2)) break;
                    }
                }

                LibFuse3LowLevel.fuse_reply_buf(req, buffer, (nuint)used);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static unsafe bool AddDot(IntPtr req, byte* buffer, ref int used, int capacity, string name, ulong ino, long next)
    {
        var entry = new FuseEntryParam { Ino = 0 };
        entry.Attr.Inode = ino;
        entry.Attr.Mode = LibcStat.Directory | 0x1ED;
        return Add(req, buffer, ref used, capacity, name, entry, next);
    }

    // fuse_add_direntry_plus returns the size the entry needs and writes it only when it fits.
    private static unsafe bool Add(IntPtr req, byte* buffer, ref int used, int capacity, string name, FuseEntryParam entry, long next)
    {
        var bytes = Utf8Z(name);
        fixed (byte* nameBytes = bytes)
        {
            var needed = LibFuse3LowLevel.fuse_add_direntry_plus(req, buffer + used, (nuint)(capacity - used), nameBytes, &entry, next);
            if (needed > (nuint)(capacity - used)) return false;
            used += (int)needed;
            return true;
        }
    }

    private unsafe void DoOpen(IntPtr req, ulong ino, FuseFileInfo* fi)
    {
        var result = _operations.Open(ino, OpenFlagsOf(fi->Flags));
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }

        var info = *fi;
        var handle = Register(result.Value!, &info);
        // A non-zero return means the request was interrupted and the kernel never saw the open.
        if (LibFuse3LowLevel.fuse_reply_open(req, &info) != 0) Unregister(handle);
    }

    private unsafe void DoCreate(IntPtr req, ulong parent, string name, uint mode, FuseFileInfo* fi)
    {
        var result = _operations.Create(parent, name, mode, OpenFlagsOf(fi->Flags));
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }

        var (created, file) = result.Value!;
        var info = *fi;
        var handle = Register(file, &info);
        var entry = EntryOf(created);
        if (LibFuse3LowLevel.fuse_reply_create(req, &entry, &info) != 0) Unregister(handle);
    }

    private unsafe void DoRead(IntPtr req, nuint size, long offset, FuseFileInfo* fi)
    {
        if (!_files.TryGetValue(fi->Fh, out var file)) { ReplyError(req, Errno.EBADF); return; }
        var result = _operations.Read(file, (ulong)offset, (int)size);
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }

        var reply = result.Value;
        if (reply.Length == 0) { LibFuse3LowLevel.fuse_reply_buf(req, null, 0); return; }

        // The kernel pulls the bytes straight from the file's descriptor; nothing passes through managed memory.
        // FdSeek makes libfuse read at the given position instead of the descriptor's shared one.
        var held = false;
        try
        {
            reply.Handle.DangerousAddRef(ref held);
            var bufvec = FuseBufvec.Init((nuint)reply.Length);
            bufvec.Buf.Flags = FuseBufFlags.IsFd | FuseBufFlags.FdSeek;
            bufvec.Buf.Fd = (int)reply.Handle.DangerousGetHandle();
            bufvec.Buf.Pos = (long)reply.Offset;
            LibFuse3LowLevel.fuse_reply_data(req, &bufvec, FuseBufCopyFlags.SpliceMove);
        }
        finally
        {
            if (held) reply.Handle.DangerousRelease();
        }
    }

    private unsafe void DoWrite(IntPtr req, byte* data, nuint size, long offset, FuseFileInfo* fi)
    {
        if (!_files.TryGetValue(fi->Fh, out var file)) { ReplyError(req, Errno.EBADF); return; }
        var result = _operations.Write(file, (ulong)offset, new ReadOnlySpan<byte>(data, (int)size));
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }
        LibFuse3LowLevel.fuse_reply_write(req, (nuint)result.Value);
    }

    private unsafe void DoRelease(IntPtr req, FuseFileInfo* fi)
    {
        Unregister(fi->Fh);
        LibFuse3LowLevel.fuse_reply_err(req, 0);
    }

    private unsafe void DoMkDir(IntPtr req, ulong parent, string name, uint mode)
    {
        var result = _operations.MkDir(parent, name, mode);
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }
        var entry = EntryOf(result.Value);
        LibFuse3LowLevel.fuse_reply_entry(req, &entry);
    }

    private static void ReplyResult(IntPtr req, VfsResult result)
    {
        if (result.Ok) LibFuse3LowLevel.fuse_reply_err(req, 0);
        else ReplyError(req, result.Error!.Value);
    }

    private void DoRename(IntPtr req, ulong parent, string name, ulong newParent, string newName, uint flags)
    {
        // RENAME_NOREPLACE, RENAME_EXCHANGE and RENAME_WHITEOUT have no meaning for the tree.
        if (flags != 0) { LibFuse3LowLevel.fuse_reply_err(req, EINVAL); return; }
        ReplyResult(req, _operations.Rename(parent, name, newParent, newName));
    }

    private unsafe void DoStatFs(IntPtr req)
    {
        var result = _operations.StatFs();
        if (!result.Ok) { ReplyError(req, result.Error!.Value); return; }

        var fs = result.Value;
        var stat = new StatVfs
        {
            BlockSize = fs.BlockSize,
            FragmentSize = fs.BlockSize,
            Blocks = fs.Blocks,
            BlocksFree = fs.BlocksFree,
            BlocksAvailable = fs.BlocksFree,
            Files = fs.Files,
            NameMax = 255,
        };
        LibFuse3LowLevel.fuse_reply_statfs(req, &stat);
    }

    private unsafe void DoInit(FuseConnInfo* conn)
    {
        conn->MaxWrite = MaxWrite;
        // max_read stays at the library's value: FuseMountArguments explains why.
        var passthrough = LibFuse3LowLevel.fuse_get_feature_flag(conn, FuseCapabilities.Passthrough);
        if (passthrough) LibFuse3LowLevel.fuse_set_feature_flag(conn, FuseCapabilities.Passthrough);
        _negotiated = new FuseSessionInfo(_soname, _fuseVersion, conn->ProtoMajor, conn->ProtoMinor, conn->MaxWrite, passthrough);
    }

    private void DoDestroy()
    {
        // The session is going away; close whatever the kernel never released.
        foreach (var handle in _files.Keys) Unregister(handle);
    }

    // ---- libfuse entry points ----------------------------------------------------------------------------

    private static unsafe FuseLowlevelOps BuildOperations() => new()
    {
        Init = (IntPtr)(delegate* unmanaged<IntPtr, FuseConnInfo*, void>)&InitCallback,
        Destroy = (IntPtr)(delegate* unmanaged<IntPtr, void>)&DestroyCallback,
        Lookup = (IntPtr)(delegate* unmanaged<IntPtr, ulong, byte*, void>)&LookupCallback,
        Forget = (IntPtr)(delegate* unmanaged<IntPtr, ulong, ulong, void>)&ForgetCallback,
        ForgetMulti = (IntPtr)(delegate* unmanaged<IntPtr, nuint, FuseForgetData*, void>)&ForgetMultiCallback,
        Getattr = (IntPtr)(delegate* unmanaged<IntPtr, ulong, FuseFileInfo*, void>)&GetAttrCallback,
        Setattr = (IntPtr)(delegate* unmanaged<IntPtr, ulong, LibcStat.Buffer*, int, FuseFileInfo*, void>)&SetAttrCallback,
        Readdirplus = (IntPtr)(delegate* unmanaged<IntPtr, ulong, nuint, long, FuseFileInfo*, void>)&ReadDirPlusCallback,
        Opendir = (IntPtr)(delegate* unmanaged<IntPtr, ulong, FuseFileInfo*, void>)&OpenDirCallback,
        Releasedir = (IntPtr)(delegate* unmanaged<IntPtr, ulong, FuseFileInfo*, void>)&ReplyOkCallback,
        Open = (IntPtr)(delegate* unmanaged<IntPtr, ulong, FuseFileInfo*, void>)&OpenCallback,
        Create = (IntPtr)(delegate* unmanaged<IntPtr, ulong, byte*, uint, FuseFileInfo*, void>)&CreateCallback,
        Read = (IntPtr)(delegate* unmanaged<IntPtr, ulong, nuint, long, FuseFileInfo*, void>)&ReadCallback,
        Write = (IntPtr)(delegate* unmanaged<IntPtr, ulong, byte*, nuint, long, FuseFileInfo*, void>)&WriteCallback,
        Release = (IntPtr)(delegate* unmanaged<IntPtr, ulong, FuseFileInfo*, void>)&ReleaseCallback,
        Flush = (IntPtr)(delegate* unmanaged<IntPtr, ulong, FuseFileInfo*, void>)&ReplyOkCallback,
        Fsync = (IntPtr)(delegate* unmanaged<IntPtr, ulong, int, FuseFileInfo*, void>)&FsyncCallback,
        Mkdir = (IntPtr)(delegate* unmanaged<IntPtr, ulong, byte*, uint, void>)&MkDirCallback,
        Unlink = (IntPtr)(delegate* unmanaged<IntPtr, ulong, byte*, void>)&UnlinkCallback,
        Rmdir = (IntPtr)(delegate* unmanaged<IntPtr, ulong, byte*, void>)&RmDirCallback,
        Rename = (IntPtr)(delegate* unmanaged<IntPtr, ulong, byte*, ulong, byte*, uint, void>)&RenameCallback,
        Statfs = (IntPtr)(delegate* unmanaged<IntPtr, ulong, void>)&StatFsCallback,
        // access is left out: default_permissions makes the kernel check modes itself.
    };

    // init and destroy get the userdata pointer itself, not a request.
    [UnmanagedCallersOnly]
    private static unsafe void InitCallback(IntPtr userdata, FuseConnInfo* conn)
    {
        var mount = (LibFuseLowLevelMount)GCHandle.FromIntPtr(userdata).Target!;
        try { mount.DoInit(conn); }
        catch (Exception e) { mount._logger.LogError(e, "FUSE init threw"); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void DestroyCallback(IntPtr userdata)
    {
        var mount = (LibFuseLowLevelMount)GCHandle.FromIntPtr(userdata).Target!;
        try { mount.DoDestroy(); }
        catch (Exception e) { mount._logger.LogError(e, "FUSE destroy threw"); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void LookupCallback(IntPtr req, ulong parent, byte* name)
    {
        var mount = Of(req);
        try { mount.DoLookup(req, parent, Name(name)); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    // forget and forget_multi are never answered with an error: the kernel waits for no reply.
    [UnmanagedCallersOnly]
    private static unsafe void ForgetCallback(IntPtr req, ulong ino, ulong lookups)
    {
        var mount = Of(req);
        try { mount._operations.Forget(ino, lookups); }
        catch (Exception e) { mount._logger.LogError(e, "FUSE forget threw"); }
        finally { LibFuse3LowLevel.fuse_reply_none(req); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void ForgetMultiCallback(IntPtr req, nuint count, FuseForgetData* forgets)
    {
        var mount = Of(req);
        try
        {
            for (nuint i = 0; i < count; i++) mount._operations.Forget(forgets[i].Ino, forgets[i].NLookup);
        }
        catch (Exception e) { mount._logger.LogError(e, "FUSE forget_multi threw"); }
        finally { LibFuse3LowLevel.fuse_reply_none(req); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void GetAttrCallback(IntPtr req, ulong ino, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoGetAttr(req, ino); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void SetAttrCallback(IntPtr req, ulong ino, LibcStat.Buffer* attr, int toSet, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoSetAttr(req, ino, attr, toSet); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void ReadDirPlusCallback(IntPtr req, ulong ino, nuint size, long offset, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoReadDirPlus(req, ino, size, offset); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    // Directories keep no per-open state, so the handle is 0.
    [UnmanagedCallersOnly]
    private static unsafe void OpenDirCallback(IntPtr req, ulong ino, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try
        {
            var info = *fi;
            info.Fh = 0;
            LibFuse3LowLevel.fuse_reply_open(req, &info);
        }
        catch (Exception e) { mount.Fail(req, e); }
    }

    // releasedir and flush: nothing to do, and success is the right answer.
    [UnmanagedCallersOnly]
    private static unsafe void ReplyOkCallback(IntPtr req, ulong ino, FuseFileInfo* fi) => LibFuse3LowLevel.fuse_reply_err(req, 0);

    [UnmanagedCallersOnly]
    private static unsafe void FsyncCallback(IntPtr req, ulong ino, int dataSync, FuseFileInfo* fi) => LibFuse3LowLevel.fuse_reply_err(req, 0);

    [UnmanagedCallersOnly]
    private static unsafe void OpenCallback(IntPtr req, ulong ino, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoOpen(req, ino, fi); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void CreateCallback(IntPtr req, ulong parent, byte* name, uint mode, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoCreate(req, parent, Name(name), mode, fi); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void ReadCallback(IntPtr req, ulong ino, nuint size, long offset, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoRead(req, size, offset, fi); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void WriteCallback(IntPtr req, ulong ino, byte* data, nuint size, long offset, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoWrite(req, data, size, offset, fi); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void ReleaseCallback(IntPtr req, ulong ino, FuseFileInfo* fi)
    {
        var mount = Of(req);
        try { mount.DoRelease(req, fi); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void MkDirCallback(IntPtr req, ulong parent, byte* name, uint mode)
    {
        var mount = Of(req);
        try { mount.DoMkDir(req, parent, Name(name), mode); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void UnlinkCallback(IntPtr req, ulong parent, byte* name)
    {
        var mount = Of(req);
        try { ReplyResult(req, mount._operations.Unlink(parent, Name(name))); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void RmDirCallback(IntPtr req, ulong parent, byte* name)
    {
        var mount = Of(req);
        try { ReplyResult(req, mount._operations.RmDir(parent, Name(name))); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void RenameCallback(IntPtr req, ulong parent, byte* name, ulong newParent, byte* newName, uint flags)
    {
        var mount = Of(req);
        try { mount.DoRename(req, parent, Name(name), newParent, Name(newName), flags); }
        catch (Exception e) { mount.Fail(req, e); }
    }

    [UnmanagedCallersOnly]
    private static unsafe void StatFsCallback(IntPtr req, ulong ino)
    {
        var mount = Of(req);
        try { mount.DoStatFs(req); }
        catch (Exception e) { mount.Fail(req, e); }
    }
}
