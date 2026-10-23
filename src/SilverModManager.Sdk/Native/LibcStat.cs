using System.Runtime.InteropServices;

namespace NexusMods.Sdk.Native;

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
    /// three reserved words at the end. Other architectures lay it out differently.
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
    /// Follows symlinks, like <c>stat</c>. Returns 0 and fills <paramref name="buffer"/>, or the errno.
    /// </summary>
    public static int TryStat(string path, out Buffer buffer)
    {
        RequireX64();
        return stat(path, out buffer) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>
    /// Does not follow a final symlink, like <c>lstat</c>, so the result is the link's own identity.
    /// Returns 0 and fills <paramref name="buffer"/>, or the errno.
    /// </summary>
    public static int TryLStat(string path, out Buffer buffer)
    {
        RequireX64();
        return lstat(path, out buffer) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    private static void RequireX64()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException($"The struct stat layout here is x86_64 only, this process is {RuntimeInformation.ProcessArchitecture}.");
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int stat(string path, out Buffer buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int lstat(string path, out Buffer buffer);
}
