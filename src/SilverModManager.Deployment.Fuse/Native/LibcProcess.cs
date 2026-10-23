using System.Runtime.InteropServices;

namespace SilverModManager.Deployment.Fuse.Native;

/// <summary>The few libc calls the mount needs that .NET does not expose.</summary>
internal static unsafe partial class LibcProcess
{
    private const int PathMax = 4096;

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr realpath(string path, byte* resolved);

    /// <summary>
    /// The path with every symlink resolved, which is how /proc/self/mountinfo spells a mount point (<c>/home</c> is a
    /// link to <c>/var/home</c> on ostree systems, so the path the caller holds and the one the kernel lists differ).
    /// </summary>
    internal static string RealPath(string path)
    {
        var buffer = stackalloc byte[PathMax];
        if (realpath(path, buffer) == IntPtr.Zero)
            throw new IOException($"realpath(`{path}`) failed with errno {Marshal.GetLastPInvokeError()}");
        return Marshal.PtrToStringUTF8((IntPtr)buffer)
               ?? throw new IOException($"realpath(`{path}`) returned no path");
    }
}
