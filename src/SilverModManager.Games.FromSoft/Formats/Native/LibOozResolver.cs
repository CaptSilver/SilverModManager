using System.Reflection;
using System.Runtime.InteropServices;

namespace SilverModManager.Games.FromSoft.Formats.Native;

/// <summary>
/// Finds the bundled libooz.so for <see cref="LibOoz"/>. The runtime's default probing does not look in
/// <c>runtimes/linux-x64/native</c> for a library the app loads itself.
/// </summary>
internal static class LibOozResolver
{
    internal const string RelativePath = "runtimes/linux-x64/native/libooz.so";

    internal const string NotFoundMessage =
        "libooz.so was not found next to the FromSoft assembly (runtimes/linux-x64/native) or on the library path. " +
        "It is built from extern/ooz by scripts/build-ooz.sh and committed under src/SilverModManager.Games.FromSoft/runtimes/linux-x64/native.";

    private static readonly Lazy<bool> Installed = new(() =>
    {
        NativeLibrary.SetDllImportResolver(typeof(LibOoz).Assembly, Resolve);
        return true;
    });

    internal static void Install() => _ = Installed.Value;

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibOoz.LibraryName) return IntPtr.Zero;

        var directory = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(directory) && NativeLibrary.TryLoad(Path.Combine(directory, RelativePath), out var bundled))
            return bundled;

        if (NativeLibrary.TryLoad("libooz.so", assembly, searchPath, out var system))
            return system;

        throw new DllNotFoundException(NotFoundMessage);
    }
}
