using System.Reflection;

namespace SilverModManager.Sdk.Native;

/// <summary>
/// Loads a bundled native library and looks up its exports. A seam so a test can stand in for the library: a
/// <see cref="NativeLibrary"/> call cannot be faked, and a real library cannot be made to lack one export.
/// </summary>
public interface INativeLibraryLoader
{
    /// <summary>Loads <paramref name="fileName"/>. <paramref name="pathsTried"/> lists every place looked, loaded or not.</summary>
    bool TryLoad(string fileName, out nint handle, out IReadOnlyList<string> pathsTried);

    bool TryGetExport(nint handle, string name, out nint address);
}

/// <summary>The real loader: <see cref="BundledNativeLibrary"/> for the file, <see cref="System.Runtime.InteropServices.NativeLibrary"/> for exports.</summary>
/// <param name="assembly">The assembly whose folder the library ships beside.</param>
/// <param name="runtimeIdentifiers">The runtime identifiers to look under; the running platform's when omitted.</param>
public sealed class BundledNativeLibraryLoader(Assembly assembly, IReadOnlyList<string>? runtimeIdentifiers = null) : INativeLibraryLoader
{
    public bool TryLoad(string fileName, out nint handle, out IReadOnlyList<string> pathsTried)
        => BundledNativeLibrary.TryLoad(fileName, assembly, null, runtimeIdentifiers ?? BundledNativeLibrary.RuntimeIdentifiers(), out handle, out pathsTried);

    public bool TryGetExport(nint handle, string name, out nint address)
        => System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, name, out address) && address != 0;
}
