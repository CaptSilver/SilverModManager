using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

/// <summary>
/// File-type checks that are safe before a read. Opening a named pipe for reading blocks until something writes
/// to it, so a condition that names one has to be turned away before any read.
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
    /// Opens <paramref name="path"/> for reading when it is a regular file once every symbolic link on it is followed,
    /// and returns null when it is missing or is anything else (a pipe, socket, device or folder). The deployment
    /// backend that links files points them at the staging store, outside the game folder, so where a link ends does
    /// not matter, only what it ends at. The path is opened non-blocking (which returns at once for a pipe) and the
    /// type is read from the opened handle, and that same handle backs the returned stream, so a path swapped after
    /// the check cannot change what is read. A file that exists but may not be read raises
    /// <see cref="UnauthorizedAccessException"/>, as any other open would.
    /// </summary>
    public static FileStream? Open(string path)
    {
        // A NUL ends the string for libc but not for a managed open, so the two would judge different files.
        if (path.Contains('\0')) return null;

        // Cheap pre-check so a device node is never opened.
        if (TypeOf(AtFdcwd, path, 0) != TypeRegular) return null;

        var handle = Open(path, ReadOnly | NonBlocking | CloseOnExec);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == PermissionDenied) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            return null;
        }

        if (TypeOf(handle, string.Empty, AtEmptyPath) != TypeRegular)
        {
            handle.Dispose();
            return null;
        }

        return new FileStream(handle, FileAccess.Read, bufferSize: 0);
    }

    private static uint TypeOf(SafeHandle directory, string path, int flags)
    {
        var added = false;
        try
        {
            directory.DangerousAddRef(ref added);
            return TypeOf((int)directory.DangerousGetHandle(), path, flags);
        }
        finally
        {
            if (added) directory.DangerousRelease();
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
    private static partial SafeFileHandle Open(string path, int flags);
}
