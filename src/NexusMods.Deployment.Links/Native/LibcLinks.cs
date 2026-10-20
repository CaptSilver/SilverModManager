using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NexusMods.Deployment.Links.Native;

/// <summary>
/// The libc calls the link backends need, each wrapped to return 0 or the errno instead of throwing,
/// like <c>LibcStat.TryStat</c>. A failed link is an ordinary answer here (EXDEV across devices,
/// EOPNOTSUPP without reflinks), so callers branch on the number.
/// </summary>
internal static partial class LibcLinks
{
    internal const int EXDEV = 18;
    internal const int EEXIST = 17;
    internal const int EOPNOTSUPP = 95;

    // _IOW(0x94, 9, int): direction write (1 << 30), size 4 (4 << 16), type 0x94 (<< 8), number 9.
    // An unsigned long in the kernel headers, so it stays a nuint rather than an int that sign-extends.
    private const nuint FICLONE = 0x40049409;

    private const int BufferSize = 4096;

    /// <summary>Creates a hard link at <paramref name="newPath"/>. Returns 0 or the errno.</summary>
    internal static int TryLink(string existing, string newPath) =>
        link(existing, newPath) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Creates a symlink at <paramref name="linkPath"/> holding <paramref name="target"/> verbatim. Returns 0 or the errno.</summary>
    internal static int TrySymlink(string target, string linkPath) =>
        symlink(target, linkPath) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>
    /// Reads the text stored in a symlink without resolving it. Returns 0 or the errno (EINVAL when
    /// <paramref name="path"/> is not a symlink).
    /// </summary>
    internal static int TryReadLink(string path, out string target)
    {
        // readlink does not NUL-terminate and truncates silently, so grow until the result fits.
        var buffer = new byte[BufferSize];
        while (true)
        {
            var length = readlink(path, buffer, (nuint)buffer.Length);
            if (length < 0)
            {
                target = string.Empty;
                return Marshal.GetLastPInvokeError();
            }

            if (length < buffer.Length)
            {
                target = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
                return 0;
            }

            buffer = new byte[buffer.Length * 2];
        }
    }

    /// <summary>
    /// Makes <paramref name="destination"/> a copy-on-write clone of <paramref name="source"/>.
    /// Returns 0 or the errno (EOPNOTSUPP on tmpfs and ext4, EXDEV across filesystems).
    /// </summary>
    internal static int TryClone(SafeFileHandle source, SafeFileHandle destination) =>
        ioctl(destination.DangerousGetHandle().ToInt32(), FICLONE, source.DangerousGetHandle().ToInt32()) == 0
            ? 0
            : Marshal.GetLastPInvokeError();

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int link(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "symlink", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int symlink(string target, string linkPath);

    [LibraryImport("libc", EntryPoint = "readlink", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint readlink(string path, Span<byte> buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, int sourceFd);
}
