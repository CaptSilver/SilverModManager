using System.Runtime.InteropServices;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>Reads the immutable attribute of a file. Clearing it needs root and goes through <see cref="ElevatedCommandRunner"/>.</summary>
public interface IImmutableFlagProbe
{
    bool IsImmutable(AbsolutePath file);
}

/// <summary>Reads the attribute with <c>FS_IOC_GETFLAGS</c>, the call <c>lsattr</c> makes.</summary>
internal sealed class IoctlImmutableFlagProbe : IImmutableFlagProbe
{
    // _IOR('f', 1, long) on 64-bit Linux, from linux/fs.h.
    private const nuint FsIocGetFlags = 0x80086601;
    private const long FsImmutableFlag = 0x00000010;

    private const int NotATypewriter = 25;
    private const int NotSupported = 95;

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, nuint request, ref long flags);

    public bool IsImmutable(AbsolutePath file)
    {
        var path = file.GetFullPath();
        if (!File.Exists(path)) return false;

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long flags = 0;
        if (Ioctl((int)handle.DangerousGetHandle(), FsIocGetFlags, ref flags) != 0)
        {
            // A file system without attributes (tmpfs on older kernels, FAT, some network mounts) has none to be set.
            var error = Marshal.GetLastPInvokeError();
            if (error is NotATypewriter or NotSupported) return false;
            throw new IOException($"Reading the attributes of {path} failed with error {error}.");
        }

        return (flags & FsImmutableFlag) != 0;
    }
}
