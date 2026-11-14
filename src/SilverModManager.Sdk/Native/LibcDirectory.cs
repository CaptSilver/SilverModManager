using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SilverModManager.Sdk.Native;

/// <summary>
/// Directory-descriptor calls (<c>openat</c>, <c>fstatat</c>, <c>fdopendir</c>) for code that must keep
/// reaching a folder after something is mounted over its path: a descriptor opened beforehand still
/// names the original directory, a path no longer does. Each call returns 0 or the errno instead of
/// throwing, like <see cref="LibcStat"/>.
/// </summary>
public static unsafe partial class LibcDirectory
{
    /// <summary>The access-mode bits of <c>open(2)</c> flags, the same on every Linux CPU.</summary>
    public const int ReadOnly = 0;
    public const int WriteOnly = 1;
    public const int ReadWrite = 2;

    private const int O_PATH = 0x200000;
    private const int O_CLOEXEC = 0x80000;
    private const int F_GETFD = 1;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int PathMax = 4096;

    private const byte DT_UNKNOWN = 0;
    private const byte DT_DIR = 4;
    private const byte DT_LNK = 10;
    private const int DirentNameOffset = 19;

    // The two CPUs this app runs on disagree on these two bits.
    private static bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    private static int O_DIRECTORY => IsArm64 ? 0x4000 : 0x10000;
    private static int O_NOFOLLOW => IsArm64 ? 0x8000 : 0x20000;

    /// <summary>
    /// Opens <paramref name="path"/> (following symlinks, since this is how a root is named) as a
    /// descriptor that can anchor <see cref="TryOpenAt"/> and <see cref="TryStatAt"/> but not be read.
    /// </summary>
    public static int TryOpenDirectory(string path, out SafeFileHandle? handle) =>
        Wrap(open(path, O_PATH | O_DIRECTORY | O_CLOEXEC, 0), out handle);

    /// <summary>
    /// Opens <paramref name="relative"/> below <paramref name="dir"/>, following symlinks, as a
    /// directory descriptor. Used once, before a mount, to resolve a symlinked directory.
    /// </summary>
    public static int TryOpenDirectoryFollowing(SafeFileHandle dir, string relative, out SafeFileHandle? handle) =>
        WrapAt(dir, relative, O_PATH | O_DIRECTORY | O_CLOEXEC, out handle);

    /// <summary>
    /// <c>openat</c> that refuses a symlink as the last component (ELOOP) and sets close-on-exec.
    /// <paramref name="flags"/> is <see cref="ReadOnly"/>, <see cref="WriteOnly"/> or <see cref="ReadWrite"/>.
    /// Components before the last are still followed; a caller that must not follow any walks them one at a time.
    /// </summary>
    public static int TryOpenAt(SafeFileHandle dir, string relative, int flags, out SafeFileHandle? file) =>
        WrapAt(dir, relative, flags | O_CLOEXEC | O_NOFOLLOW, out file);

    /// <summary>Like <see cref="TryOpenAt"/>, but for a directory: an <c>O_PATH</c> descriptor, never followed.</summary>
    public static int TryOpenDirectoryAt(SafeFileHandle dir, string relative, out SafeFileHandle? handle) =>
        WrapAt(dir, relative, O_PATH | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW, out handle);

    /// <summary><c>fstatat</c>. Returns 0 and fills <paramref name="buffer"/>, or the errno.</summary>
    public static int TryStatAt(SafeFileHandle dir, string relative, bool followSymlinks, out LibcStat.Buffer buffer)
    {
        var flags = followSymlinks ? 0 : AT_SYMLINK_NOFOLLOW;
        if (relative.Length == 0) flags |= AT_EMPTY_PATH;

        bool added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            var fd = (int)dir.DangerousGetHandle();
            if (IsArm64)
            {
                if (fstatatArm64(fd, relative, out var raw, flags) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    buffer = default;
                    return errno;
                }
                buffer = LibcStat.ToBuffer(raw);
                return 0;
            }

            return fstatat(fd, relative, out buffer, flags) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (added) dir.DangerousRelease();
        }
    }

    /// <summary>
    /// The entries of the directory at <paramref name="relative"/> below <paramref name="dir"/> (empty for
    /// <paramref name="dir"/> itself), without <c>.</c> and <c>..</c>. A filesystem that does not report a
    /// type has it read with <c>fstatat</c>. Throws <see cref="IOException"/> on failure.
    /// </summary>
    public static IReadOnlyList<(string Name, bool IsDirectory, bool IsSymlink)> Enumerate(SafeFileHandle dir, string relative)
    {
        var listing = relative.Length == 0 ? "." : relative;
        var errno = WrapAt(dir, listing, 0 | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW, out var opened);
        if (errno != 0 || opened is null) throw new IOException($"Opening directory `{relative}` failed with errno {errno}.");

        // fdopendir takes over the descriptor, so the handle must not close it as well.
        var fd = (int)opened.DangerousGetHandle();
        opened.SetHandleAsInvalid();
        var stream = fdopendir(fd);
        if (stream == IntPtr.Zero)
        {
            var failure = Marshal.GetLastPInvokeError();
            close(fd);
            throw new IOException($"fdopendir of `{relative}` failed with errno {failure}.");
        }

        var result = new List<(string, bool, bool)>();
        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = readdir(stream);
                if (entry == null)
                {
                    var failure = Marshal.GetLastPInvokeError();
                    if (failure != 0) throw new IOException($"readdir of `{relative}` failed with errno {failure}.");
                    break;
                }

                var name = Marshal.PtrToStringUTF8((IntPtr)(entry + DirentNameOffset)) ?? string.Empty;
                if (name is "." or "..") continue;

                var type = entry[18];
                if (type == DT_UNKNOWN)
                {
                    var path = relative.Length == 0 ? name : $"{relative}/{name}";
                    var statErrno = TryStatAt(dir, path, followSymlinks: false, out var stat);
                    if (statErrno == 2) continue; // gone since it was listed
                    if (statErrno != 0) throw new IOException($"fstatat of `{path}` failed with errno {statErrno}.");
                    result.Add((name, stat.IsDirectory, (stat.Mode & LibcStat.TypeMask) == LibcStat.Symlink));
                }
                else
                {
                    result.Add((name, type == DT_DIR, type == DT_LNK));
                }
            }
        }
        finally
        {
            closedir(stream);
        }

        return result;
    }

    /// <summary>The flags of the descriptor (<c>F_GETFD</c>), so a caller can check <c>FD_CLOEXEC</c>. Returns 0 or the errno.</summary>
    public static int TryGetFdFlags(SafeFileHandle handle, out int flags)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            flags = fcntl((int)handle.DangerousGetHandle(), F_GETFD);
            if (flags >= 0) return 0;
            flags = 0;
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    /// <summary>The path with every symlink resolved. Returns 0 or the errno.</summary>
    public static int TryRealPath(string path, out string resolved)
    {
        var buffer = stackalloc byte[PathMax];
        if (realpath(path, buffer) == IntPtr.Zero)
        {
            resolved = string.Empty;
            return Marshal.GetLastPInvokeError();
        }

        resolved = Marshal.PtrToStringUTF8((IntPtr)buffer) ?? string.Empty;
        return 0;
    }

    private static int Wrap(int fd, out SafeFileHandle? handle)
    {
        if (fd < 0)
        {
            handle = null;
            return Marshal.GetLastPInvokeError();
        }

        handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        return 0;
    }

    private static int WrapAt(SafeFileHandle dir, string relative, int flags, out SafeFileHandle? handle)
    {
        bool added = false;
        try
        {
            dir.DangerousAddRef(ref added);
            return Wrap(openat((int)dir.DangerousGetHandle(), relative, flags, 0), out handle);
        }
        finally
        {
            if (added) dir.DangerousRelease();
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int openat(int dirfd, string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "fstatat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int fstatat(int dirfd, string path, out LibcStat.Buffer buffer, int flags);

    [LibraryImport("libc", EntryPoint = "fstatat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int fstatatArm64(int dirfd, string path, out LibcStat.Arm64Layout buffer, int flags);

    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial IntPtr fdopendir(int fd);

    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial byte* readdir(IntPtr stream);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int closedir(IntPtr stream);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int close(int fd);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int fcntl(int fd, int command);

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr realpath(string path, byte* resolved);
}
