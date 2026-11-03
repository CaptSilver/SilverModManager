using NexusMods.Paths;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Reads the two things the diagnostics need from a Windows DLL or executable without running it: the libraries
/// it imports and the file version in its version resource. A seam so the emitters can be tested without real
/// binaries.
/// </summary>
public interface INativeImageReader
{
    /// <summary>
    /// The file names of the libraries the image imports, as written in its import table. Empty for a file that is
    /// not a Windows image or imports nothing.
    /// </summary>
    IReadOnlyList<string> ImportedLibraries(AbsolutePath file);

    /// <summary>
    /// The version the image's version resource gives, or null when it has none or the file is not a Windows image.
    /// </summary>
    Version? FileVersion(AbsolutePath file);
}
