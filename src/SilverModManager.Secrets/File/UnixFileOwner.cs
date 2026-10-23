using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SilverModManager.Secrets.File;

/// <summary>
/// Who owns a file. .NET exposes the mode of a file but not its owner, and a secrets file that
/// belongs to someone else is not one we can trust, whatever its mode says.
/// </summary>
internal static unsafe partial class UnixFileOwner
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxUid = 0x8;

    // struct statx is the one stat layout that is the same on every architecture:
    // u32 mask, u32 blksize, u64 attributes, u32 nlink, u32 uid, ...
    private const int StatxMaskOffset = 0;
    private const int StatxUidOffset = 20;
    private const int StatxSize = 256;

    public static uint EffectiveUid() => GetEuid();

    /// <summary>
    /// The uid owning <paramref name="path"/> itself; a symlink reports its own owner, not its target's.
    /// </summary>
    public static uint GetOwner(string path)
    {
        var buffer = stackalloc byte[StatxSize];
        if (Statx(AtFdcwd, path, AtSymlinkNofollow, StatxUid, buffer) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not read the owner of '{path}': {new Win32Exception(errno).Message}", errno);
        }

        if ((*(uint*)(buffer + StatxMaskOffset) & StatxUid) == 0)
            throw new IOException($"The filesystem holding '{path}' did not report an owner.");
        return *(uint*)(buffer + StatxUidOffset);
    }

    public static bool IsOwnedBy(string path, uint uid) => GetOwner(path) == uid;

    public static bool IsOwnedByCurrentUser(string path) => IsOwnedBy(path, EffectiveUid());

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();
}
