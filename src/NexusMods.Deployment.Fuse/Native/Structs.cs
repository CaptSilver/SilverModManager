using System.Runtime.InteropServices;
using NexusMods.Deployment.Vfs;

namespace NexusMods.Deployment.Fuse.Native;

// Transcribed by hand from the libfuse 3.18 headers (fuse_common.h, fuse_lowlevel.h, fuse_opt.h) for
// x86_64 glibc, the combination FuseAbi.VerifiedVersion names. Every size and the offsets that matter are
// pinned in StructLayoutTests against numbers a C compiler printed. These layouts are not portable: like
// LibcStat.Buffer, nothing here checks the architecture except StructGuard, which LibFuse3LowLevel runs before the first import.

internal static class StructGuard
{
    internal static void RequireX64()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException($"The libfuse struct layouts here are x86_64 only, this process is {RuntimeInformation.ProcessArchitecture}.");
    }
}

/// <summary><c>struct fuse_args</c>: the argv libfuse parses mount options from.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FuseArgs
{
    public int Argc;
    public IntPtr Argv;
    public int Allocated;
}

/// <summary>
/// <c>struct libfuse_version</c>. Handed to <c>fuse_session_new_versioned</c> by pointer; it tells a newer libfuse
/// which <c>fuse_lowlevel_ops</c> layout the caller was compiled against.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LibfuseVersion
{
    public uint Major;
    public uint Minor;
    public uint Hotfix;
    public uint Padding;

    /// <summary>The headers the struct transcriptions in this file came from.</summary>
    public static LibfuseVersion Current => new() { Major = 3, Minor = 18, Hotfix = 3, Padding = 0 };
}

/// <summary><c>struct fuse_entry_param</c>: the reply to lookup, create and readdirplus.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FuseEntryParam
{
    public ulong Ino;
    public ulong Generation;
    public LibcStat.Buffer Attr;
    public double AttrTimeout;
    public double EntryTimeout;
}

/// <summary>
/// <c>struct fuse_file_info</c>. The ten flag members are C bitfields sharing the 32-bit word at offset 4, in
/// declaration order from bit 0; 3.18 has no passthrough bit here, passthrough is the <see cref="BackingId"/> field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FuseFileInfo
{
    public int Flags;
    private uint _bits;
    private uint _padding2;
    private uint _padding3;
    public ulong Fh;
    public ulong LockOwner;
    public uint PollEvents;
    public int BackingId;
    public ulong CompatFlags;
    private ulong _reserved0;
    private ulong _reserved1;

    public bool WritePage { readonly get => Get(0); set => Set(0, value); }
    public bool DirectIo { readonly get => Get(1); set => Set(1, value); }
    public bool KeepCache { readonly get => Get(2); set => Set(2, value); }
    public bool Flush { readonly get => Get(3); set => Set(3, value); }
    public bool NonSeekable { readonly get => Get(4); set => Set(4, value); }
    public bool FlockRelease { readonly get => Get(5); set => Set(5, value); }
    public bool CacheReaddir { readonly get => Get(6); set => Set(6, value); }
    public bool NoFlush { readonly get => Get(7); set => Set(7, value); }
    public bool ParallelDirectWrites { readonly get => Get(8); set => Set(8, value); }
    public bool KillSuidgid { readonly get => Get(9); set => Set(9, value); }

    private readonly bool Get(int bit) => (_bits & (1u << bit)) != 0;

    private void Set(int bit, bool value) => _bits = value ? _bits | (1u << bit) : _bits & ~(1u << bit);
}

/// <summary>
/// <c>struct fuse_conn_info</c>, filled by libfuse and handed to <c>init</c>. Since 3.17 the 64-bit
/// <see cref="CapableExt"/> and <see cref="WantExt"/> are the masks libfuse acts on; <c>want</c> and <c>capable</c>
/// are the 32-bit words kept for older callers. Request capabilities through <c>fuse_set_feature_flag</c> rather
/// than writing either word directly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FuseConnInfo
{
    public uint ProtoMajor;
    public uint ProtoMinor;
    public uint MaxWrite;
    public uint MaxRead;
    public uint MaxReadahead;
    public uint Capable;
    public uint Want;
    public uint MaxBackground;
    public uint CongestionThreshold;
    public uint TimeGran;
    public uint MaxBackingStackDepth;
    private uint _bits;
    public ulong CapableExt;
    public ulong WantExt;
    public ushort RequestTimeout;
    private fixed ushort _reserved[31];

    public bool NoInterrupt
    {
        readonly get => (_bits & 1u) != 0;
        set => _bits = value ? _bits | 1u : _bits & ~1u;
    }
}

/// <summary><c>enum fuse_buf_flags</c>.</summary>
[Flags]
internal enum FuseBufFlags
{
    None = 0,
    IsFd = 1 << 1,
    FdSeek = 1 << 2,
    FdRetry = 1 << 3,
}

/// <summary><c>enum fuse_buf_copy_flags</c>.</summary>
[Flags]
internal enum FuseBufCopyFlags
{
    None = 0,
    NoSplice = 1 << 1,
    ForceSplice = 1 << 2,
    SpliceMove = 1 << 3,
    SpliceNonblock = 1 << 4,
}

/// <summary><c>struct fuse_buf</c>: memory or a file descriptor range, per <see cref="FuseBufFlags.IsFd"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FuseBuf
{
    public nuint Size;
    public FuseBufFlags Flags;
    public IntPtr Mem;
    public int Fd;
    public long Pos;
    public nuint MemSize;
}

/// <summary><c>struct fuse_bufvec</c> with the C flexible array fixed at its declared one element.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FuseBufvec
{
    public nuint Count;
    public nuint Idx;
    public nuint Off;
    public FuseBuf Buf;

    /// <summary>The <c>FUSE_BUFVEC_INIT(size)</c> macro: one buffer, no memory attached yet.</summary>
    public static FuseBufvec Init(nuint size) => new() { Count = 1, Buf = new FuseBuf { Size = size, Fd = -1 } };
}

/// <summary>glibc's <c>struct statvfs</c> on x86_64: 112 bytes, ending in six reserved ints.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StatVfs
{
    public ulong BlockSize;
    public ulong FragmentSize;
    public ulong Blocks;
    public ulong BlocksFree;
    public ulong BlocksAvailable;
    public ulong Files;
    public ulong FilesFree;
    public ulong FilesAvailable;
    public ulong FilesystemId;
    public ulong MountFlags;
    public ulong NameMax;
    private fixed int _spare[6];
}

/// <summary><c>struct fuse_ctx</c>: who issued the request.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FuseCtx
{
    public uint Uid;
    public uint Gid;
    public int Pid;
    public uint Umask;
}

/// <summary>Capability bits for <c>fuse_conn_info.want</c> / <c>fuse_set_feature_flag</c>.</summary>
internal static class FuseCapabilities
{
    public const ulong AsyncRead = 1UL << 0;
    public const ulong Passthrough = 1UL << 29;
}

/// <summary>
/// <c>struct fuse_lowlevel_ops</c>: one function pointer per operation, in header order. A null pointer means the
/// operation is not implemented and libfuse answers ENOSYS. Fill with <c>Marshal.GetFunctionPointerForDelegate</c>
/// or <c>delegate* unmanaged</c> pointers. The 3.x <c>ioctl</c> slot takes an unsigned <c>cmd</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FuseLowlevelOps
{
    public IntPtr Init;
    public IntPtr Destroy;
    public IntPtr Lookup;
    public IntPtr Forget;
    public IntPtr Getattr;
    public IntPtr Setattr;
    public IntPtr Readlink;
    public IntPtr Mknod;
    public IntPtr Mkdir;
    public IntPtr Unlink;
    public IntPtr Rmdir;
    public IntPtr Symlink;
    public IntPtr Rename;
    public IntPtr Link;
    public IntPtr Open;
    public IntPtr Read;
    public IntPtr Write;
    public IntPtr Flush;
    public IntPtr Release;
    public IntPtr Fsync;
    public IntPtr Opendir;
    public IntPtr Readdir;
    public IntPtr Releasedir;
    public IntPtr Fsyncdir;
    public IntPtr Statfs;
    public IntPtr Setxattr;
    public IntPtr Getxattr;
    public IntPtr Listxattr;
    public IntPtr Removexattr;
    public IntPtr Access;
    public IntPtr Create;
    public IntPtr Getlk;
    public IntPtr Setlk;
    public IntPtr Bmap;
    public IntPtr Ioctl;
    public IntPtr Poll;
    public IntPtr WriteBuf;
    public IntPtr RetrieveReply;
    public IntPtr ForgetMulti;
    public IntPtr Flock;
    public IntPtr Fallocate;
    public IntPtr Readdirplus;
    public IntPtr CopyFileRange;
    public IntPtr Lseek;
    public IntPtr Tmpfile;
    public IntPtr Statx;
}
