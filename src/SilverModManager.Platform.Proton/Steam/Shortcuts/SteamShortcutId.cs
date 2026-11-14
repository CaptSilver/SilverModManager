using System.IO.Hashing;
using System.Text;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// The ids Steam derives for a non-Steam shortcut from its executable and name.
/// </summary>
public static class SteamShortcutId
{
    /// <summary>
    /// The 32-bit app id: CRC-32 of the quoted exe followed by the name, with the high bit set. Used only to create
    /// an entry; art is always named after the id read back from the file.
    /// </summary>
    public static uint AppId(string quotedExe, string appName)
        => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(quotedExe + appName)) | 0x8000_0000;

    /// <summary>
    /// The 64-bit id Steam uses for <c>steam://rungameid/</c> and for the big-picture grid file.
    /// </summary>
    public static ulong LaunchId(uint appId) => ((ulong)appId << 32) | 0x0200_0000;
}
