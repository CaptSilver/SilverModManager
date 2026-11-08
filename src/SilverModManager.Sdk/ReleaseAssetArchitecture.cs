using System.Runtime.InteropServices;

namespace SilverModManager.Sdk;

/// <summary>
/// How a CPU is spelled in release asset names and in the header of the ELF program inside them. Every place that
/// decides whether a download is for this machine asks here, so the spellings live in one table.
/// </summary>
public static class ReleaseAssetArchitecture
{
    /// <summary>
    /// The CPU name in an AppImage file name (<c>SilverModManager.x86_64.AppImage</c>), or null for a CPU nothing is built for.
    /// </summary>
    public static string? Token(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        _ => null,
    };

    /// <summary>
    /// The runtime identifier suffix in an archive file name (<c>App.linux-x64.zip</c>), or null for a CPU nothing is built for.
    /// </summary>
    public static string? RuntimeIdentifierSuffix(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "-x64",
        Architecture.Arm64 => "-arm64",
        _ => null,
    };

    /// <summary>
    /// The <c>e_machine</c> value an ELF program for this CPU carries (bytes 18 and 19 of the header), or null.
    /// </summary>
    public static ushort? ElfMachine(Architecture architecture) => architecture switch
    {
        Architecture.X64 => 0x3e,
        Architecture.Arm64 => 0xb7,
        _ => null,
    };
}
