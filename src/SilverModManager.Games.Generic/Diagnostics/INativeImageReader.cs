using NexusMods.Paths;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Reads the things the diagnostics need from a Windows DLL or executable without running it: the libraries it
/// imports, whether it is a 32-bit or 64-bit image, and the file version in its version resource. A seam so the
/// emitters can be tested without real binaries.
/// </summary>
public interface INativeImageReader
{
    /// <summary>
    /// The image's imports. <see cref="NativeImageImports.None"/> for a file that is not a Windows image.
    /// </summary>
    NativeImageImports ReadImports(AbsolutePath file);

    /// <summary>
    /// The version the image's version resource gives, or null when it has none or the file is not a Windows image.
    /// </summary>
    Version? FileVersion(AbsolutePath file);
}

/// <summary>
/// What an image's import table names, with the image's bitness: a 32-bit process loads its libraries from a
/// different folder of a Wine prefix than a 64-bit one.
/// </summary>
/// <param name="Libraries">The file names of the imported libraries, as written in the import table.</param>
/// <param name="Is64Bit">True for a PE32+ image, false for PE32.</param>
public sealed record NativeImageImports(IReadOnlyList<string> Libraries, bool Is64Bit)
{
    /// <summary>The result for a file that is not a Windows image or imports nothing.</summary>
    public static NativeImageImports None { get; } = new([], true);
}
