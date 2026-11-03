using System.Runtime.InteropServices;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

/// <summary>
/// File-type checks that never open the file. Opening a named pipe for reading blocks until something writes
/// to it, so a condition that names one has to be turned away before any <c>OpenRead</c>.
/// </summary>
internal static partial class RegularFiles
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const int ModeOffset = 28; // stx_mode, the same on every architecture, unlike struct stat
    private const int StatxSize = 256;
    private const uint TypeMask = 0xF000;
    private const uint TypeRegular = 0x8000;
    private const uint TypeSymlink = 0xA000;

    /// <summary>
    /// Whether <paramref name="path"/> is a regular file. A symbolic link counts only when what it finally points
    /// at is a regular file inside <paramref name="root"/>; the file type is read with lstat semantics, so a link
    /// to a pipe or to a file elsewhere is not followed.
    /// </summary>
    public static bool IsRegular(string path, string root)
    {
        var type = TypeOf(path);
        if (type == TypeRegular) return true;
        if (type != TypeSymlink) return false;

        var target = Real(path);
        var realRoot = Real(root);
        if (target is null || realRoot is null) return false;
        if (!IsInside(realRoot, target)) return false;

        return TypeOf(target) == TypeRegular;
    }

    private static bool IsInside(string root, string path) =>
        path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static uint TypeOf(string path)
    {
        Span<byte> buffer = stackalloc byte[StatxSize];
        int result;
        unsafe
        {
            fixed (byte* pointer = buffer)
                result = StatX(AtFdcwd, path, AtSymlinkNoFollow, StatxType, pointer);
        }

        if (result != 0) return 0;

        return BitConverter.ToUInt16(buffer[ModeOffset..]) & TypeMask;
    }

    private static string? Real(string path)
    {
        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) return null;

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatX(int directory, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr pointer);
}
