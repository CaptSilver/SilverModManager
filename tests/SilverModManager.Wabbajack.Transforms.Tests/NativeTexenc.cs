using System.Runtime.InteropServices;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>
/// Classes that load the real libsmmtexenc.so share one xunit collection, so they never run at once: its init fills codec
/// tables without a lock and every class that loads the library calls it.
/// </summary>
internal static class NativeTexenc
{
    public const string Collection = "native texenc";

    /// <summary>
    /// True on linux-x64 and linux-arm64, the runtimes the library is committed for. Tests expect the library there and
    /// fail when it is missing from the output; elsewhere they skip, since the managed encoder is the supported floor.
    /// </summary>
    public static bool LibraryIsBundledForThisPlatform
        => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
}
