using System.Runtime.InteropServices;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

/// <summary>
/// File-type checks that are safe before a read. Opening a named pipe for reading blocks until something writes
/// to it, so a condition that names one has to be turned away before any <c>OpenRead</c>.
/// </summary>
internal static partial class RegularFiles
{
    private const int AtFdcwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x1;
    private const int ModeOffset = 28; // stx_mode, the same on every architecture, unlike struct stat
    private const int StatxSize = 256;
    private const uint TypeMask = 0xF000;
    private const uint TypeRegular = 0x8000;
    private const int PermissionDenied = 13; // EACCES
    private const int ReadOnly = 0;
    private const int NonBlocking = 0x800;
    private const int CloseOnExec = 0x80000;

    /// <summary>
    /// Whether <paramref name="path"/> is a regular file once every symbolic link on it is followed. The deployment
    /// backend that links files points them at the staging store, outside the game folder, so where a link ends does
    /// not matter, only what it ends at. The path is opened non-blocking (which returns at once for a pipe) and the
    /// type is read from the opened handle, so nothing named by a swapped path is judged.
    /// </summary>
    public static bool IsRegular(string path)
    {
        // A NUL ends the string for libc but not for the managed open that follows, so the two would judge different files.
        if (path.Contains('\0')) return false;

        // Cheap pre-check so a device node is never opened.
        if (TypeOf(AtFdcwd, path, 0) != TypeRegular) return false;

        var descriptor = Open(path, ReadOnly | NonBlocking | CloseOnExec);
        if (descriptor < 0)
        {
            // The pre-check saw a regular file, so a refused open is a permission problem. Let the caller's own open
            // report it, as it does for any unreadable file, instead of passing it off as a missing one.
            return Marshal.GetLastPInvokeError() == PermissionDenied;
        }

        try
        {
            return TypeOf(descriptor, string.Empty, AtEmptyPath) == TypeRegular;
        }
        finally
        {
            Close(descriptor);
        }
    }

    private static uint TypeOf(int directory, string path, int flags)
    {
        Span<byte> buffer = stackalloc byte[StatxSize];
        int result;
        unsafe
        {
            fixed (byte* pointer = buffer)
                result = StatX(directory, path, flags, StatxType, pointer);
        }

        if (result != 0) return 0;

        return BitConverter.ToUInt16(buffer[ModeOffset..]) & TypeMask;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatX(int directory, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}
