using System.Runtime.InteropServices;

namespace SilverModManager.Secrets.File;

/// <summary>
/// What the current user may do in a directory. .NET has no way to ask without trying, and a secrets store
/// should not create a file just to report its status.
/// </summary>
internal static partial class UnixFileAccess
{
    private const int AtFdcwd = -100;
    private const int AtEaccess = 0x200;
    private const int ReadOk = 4;
    private const int WriteOk = 2;
    private const int ExecuteOk = 1;

    /// <summary>
    /// True when the effective user can create and rename files in <paramref name="directory"/>.
    /// </summary>
    public static bool CanCreateIn(string directory) => FAccessAt(AtFdcwd, directory, WriteOk | ExecuteOk, AtEaccess) == 0;

    /// <summary>
    /// True when the effective user can open the existing file <paramref name="path"/> for reading and writing.
    /// </summary>
    public static bool CanReadWrite(string path) => FAccessAt(AtFdcwd, path, ReadOk | WriteOk, AtEaccess) == 0;

    [LibraryImport("libc", EntryPoint = "faccessat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int FAccessAt(int dirfd, string path, int mode, int flags);
}
