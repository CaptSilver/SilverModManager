using System.Reflection;
using System.Runtime.InteropServices;

namespace SilverModManager.Deployment.Fuse.Native;

/// <summary>
/// Finds libfuse3 for <see cref="LibFuse3"/>. Fedora's fuse3-libs ships only the versioned library and the
/// <c>.so.4</c> symlink (the unversioned <c>libfuse3.so</c> needs fuse3-devel), so the runtime's default
/// probing for "fuse3" finds nothing.
/// </summary>
internal static class LibFuse3Resolver
{
    /// <summary>
    /// <c>.so.3</c> is only probed so a machine that has just that can be refused with a precise message;
    /// <see cref="FuseAbi"/> never accepts it.
    /// </summary>
    internal static readonly string[] ProbeOrder = ["libfuse3.so.4", "libfuse3.so.3"];

    private static readonly Lazy<bool> Installed = new(() =>
    {
        NativeLibrary.SetDllImportResolver(typeof(LibFuse3).Assembly, Resolve);
        return true;
    });

    /// <summary>The soname the last successful resolve loaded, or null when nothing has been loaded.</summary>
    internal static string? LoadedSoname { get; private set; }

    internal static void Install() => _ = Installed.Value;

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibFuse3.LibraryName) return IntPtr.Zero;

        foreach (var soname in ProbeOrder)
        {
            if (!NativeLibrary.TryLoad(soname, out var handle)) continue;
            LoadedSoname = soname;
            return handle;
        }

        // Fall through to the default probing, which raises the DllNotFoundException.
        return IntPtr.Zero;
    }
}
