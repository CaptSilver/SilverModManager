using System.Reflection;
using System.Runtime.InteropServices;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Games.FromSoft.Formats.Native;

/// <summary>
/// Finds the bundled libooz.so for <see cref="LibOoz"/>; the lookup is <see cref="BundledNativeLibrary"/>'s.
/// </summary>
internal static class LibOozResolver
{
    internal const string FileName = "libooz.so";

    internal const string BuildScript = "scripts/build-ooz.sh";

    private static readonly Lazy<bool> Installed = new(() =>
    {
        NativeLibrary.SetDllImportResolver(typeof(LibOoz).Assembly, Resolve);
        return true;
    });

    internal static string NotFoundMessage(IReadOnlyList<string> pathsTried) => BundledNativeLibrary.NotFoundMessage(FileName, BuildScript, pathsTried);

    internal static void Install() => _ = Installed.Value;

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibOoz.LibraryName) return IntPtr.Zero;

        if (BundledNativeLibrary.TryLoad(FileName, assembly, searchPath, out var handle, out var tried))
            return handle;

        throw new DllNotFoundException(NotFoundMessage(tried));
    }
}
