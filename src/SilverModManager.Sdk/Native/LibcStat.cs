using System.Runtime.InteropServices;

namespace SilverModManager.Sdk.Native;

/// <summary>
/// One <c>stat(2)</c> call and the struct it fills, so every consumer reads dev, inode, ctime and the
/// rest from the same place. <c>FileSystemInfo</c> cannot give those, and mixing the two would be
/// two sources of truth for one fingerprint. A binding that has to build a <c>struct stat</c> for an
/// attribute reply reuses <see cref="Buffer"/> instead of transcribing another copy.
/// </summary>
public static partial class LibcStat
{
    public const uint TypeMask = 0xF000;
    public const uint RegularFile = 0x8000;
    public const uint Directory = 0x4000;
    public const uint Symlink = 0xA000;

    /// <summary>
    /// glibc's <c>struct stat</c> on x86_64: 144 bytes, with a 4-byte pad after <c>st_gid</c> and
    /// three reserved words at the end. This is the shape <c>LibFuseLowLevelMount</c> writes into
    /// libfuse's memory, so it stays x86_64. On other CPUs it is only the managed value
    /// <see cref="TryStat(string, Architecture, bool, out Buffer)"/> copies the real layout into.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Buffer
    {
        public ulong Device;
        public ulong Inode;
        public ulong NLink;
        public uint Mode;
        public uint Uid;
        public uint Gid;
        private int _pad0;
        public ulong RDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModifySeconds;
        public long ModifyNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        private long _reserved0;
        private long _reserved1;
        private long _reserved2;

        public readonly bool IsDirectory => (Mode & TypeMask) == Directory;
        public readonly bool IsRegularFile => (Mode & TypeMask) == RegularFile;
        public readonly DateTimeOffset MTime => ToTime(ModifySeconds, ModifyNanoseconds);
        public readonly DateTimeOffset CTime => ToTime(ChangeSeconds, ChangeNanoseconds);

        private static DateTimeOffset ToTime(long seconds, long nanoseconds) =>
            DateTimeOffset.UnixEpoch.AddSeconds(seconds).AddTicks(nanoseconds / 100);
    }

    /// <summary>
    /// glibc's <c>struct stat</c> on aarch64 (the kernel's generic layout): 128 bytes. The offsets
    /// were read from <c>offsetof</c> in a C program compiled and run in an arm64 ubuntu:20.04
    /// container (glibc 2.31) under qemu-user: dev 0, ino 8, mode 16, nlink 20, uid 24, gid 28,
    /// rdev 32, size 48, blksize 56, blocks 64, atim 72, mtim 88, ctim 104, sizeof 128.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Arm64Layout
    {
        public ulong Device;
        public ulong Inode;
        public uint Mode;
        public uint NLink;
        public uint Uid;
        public uint Gid;
        public ulong RDevice;
        private ulong _pad1;
        public long Size;
        public int BlockSize;
        private int _pad2;
        public long Blocks;
        public long AccessSeconds;
        public long AccessNanoseconds;
        public long ModifySeconds;
        public long ModifyNanoseconds;
        public long ChangeSeconds;
        public long ChangeNanoseconds;
        private int _unused0;
        private int _unused1;
    }

    /// <summary>
    /// Follows symlinks, like <c>stat</c>. Returns 0 and fills <paramref name="buffer"/>, or the errno.
    /// </summary>
    public static int TryStat(string path, out Buffer buffer) =>
        TryStat(path, RuntimeInformation.ProcessArchitecture, followSymlinks: true, out buffer);

    /// <summary>
    /// Does not follow a final symlink, like <c>lstat</c>, so the result is the link's own identity.
    /// Returns 0 and fills <paramref name="buffer"/>, or the errno.
    /// </summary>
    public static int TryLStat(string path, out Buffer buffer) =>
        TryStat(path, RuntimeInformation.ProcessArchitecture, followSymlinks: false, out buffer);

    /// <summary>
    /// The one place the CPU picks a struct layout. A CPU with no known layout is refused by name
    /// rather than read through the wrong one.
    /// </summary>
    internal static int TryStat(string path, Architecture architecture, bool followSymlinks, out Buffer buffer)
    {
        switch (architecture)
        {
            case Architecture.X64:
                return (followSymlinks ? stat(path, out buffer) : lstat(path, out buffer)) == 0 ? 0 : Marshal.GetLastPInvokeError();
            case Architecture.Arm64:
                var result = followSymlinks ? statArm64(path, out var raw) : lstatArm64(path, out raw);
                if (result != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    buffer = default;
                    return errno;
                }
                buffer = ToBuffer(raw);
                return 0;
            default:
                throw new PlatformNotSupportedException($"No struct stat layout is known for {architecture}; only X64 and Arm64 are supported.");
        }
    }

    internal static Buffer ToBuffer(in Arm64Layout raw) => new()
    {
        Device = raw.Device,
        Inode = raw.Inode,
        NLink = raw.NLink,
        Mode = raw.Mode,
        Uid = raw.Uid,
        Gid = raw.Gid,
        RDevice = raw.RDevice,
        Size = raw.Size,
        BlockSize = raw.BlockSize,
        Blocks = raw.Blocks,
        AccessSeconds = raw.AccessSeconds,
        AccessNanoseconds = raw.AccessNanoseconds,
        ModifySeconds = raw.ModifySeconds,
        ModifyNanoseconds = raw.ModifyNanoseconds,
        ChangeSeconds = raw.ChangeSeconds,
        ChangeNanoseconds = raw.ChangeNanoseconds,
    };

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int stat(string path, out Buffer buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int lstat(string path, out Buffer buffer);

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int statArm64(string path, out Arm64Layout buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int lstatArm64(string path, out Arm64Layout buffer);
}
