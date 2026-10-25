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
        "libooz.so was not found under runtimes/linux-x64/native next to the FromSoft assembly or the app, or on the library path. " +
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

        foreach (var candidate in CandidatePaths(assembly.Location, AppContext.BaseDirectory))
        {
            if (NativeLibrary.TryLoad(candidate, out var bundled)) return bundled;
        }

        if (NativeLibrary.TryLoad("libooz.so", assembly, searchPath, out var system))
            return system;

        throw new DllNotFoundException(NotFoundMessage);
    }

    /// <summary>
    /// Where the bundled library may sit: beside the assembly, then beside the app. A single-file publish (the
    /// AppImage) reports an empty <see cref="Assembly.Location"/> and unpacks native libraries beside the executable,
    /// which <see cref="AppContext.BaseDirectory"/> names.
    /// </summary>
    internal static IReadOnlyList<string> CandidatePaths(string assemblyLocation, string baseDirectory)
    {
        var folders = new List<string>();
        var assemblyFolder = Path.GetDirectoryName(assemblyLocation);
        if (!string.IsNullOrEmpty(assemblyFolder)) folders.Add(assemblyFolder);
        if (!string.IsNullOrEmpty(baseDirectory)) folders.Add(baseDirectory);

        return folders
            .Select(folder => Path.Combine(folder, RelativePath))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
