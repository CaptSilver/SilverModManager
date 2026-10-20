using System.Runtime.InteropServices;

namespace NexusMods.Deployment.Fuse.Native;

// Linux only, like the rest of the app: libfuse3 and /dev/fuse do not exist elsewhere, so there is no platform check.
internal static partial class LibFuse3
{
    // Resolved by LibFuse3Resolver: Fedora's fuse3-libs has no plain libfuse3.so without fuse3-devel.
    internal const string LibraryName = "fuse3";

    static LibFuse3() => LibFuse3Resolver.Install();

    [LibraryImport(LibraryName)]
    internal static partial int fuse_version();

    [LibraryImport(LibraryName)]
    private static partial IntPtr fuse_pkgversion();

    internal static string? PackageVersion() => Marshal.PtrToStringUTF8(fuse_pkgversion());
}
