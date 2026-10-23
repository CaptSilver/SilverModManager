// Follows GetBSAHash and GetFolderBSAHash in Wabbajack.Compression.BSA/BinaryHelperExtensions.cs (GPL-3.0).
namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// The 64 bit name hashes TES5 archives sort their folders and files by (and the games search them by).
/// The algorithm is the one on the UESP wiki, https://en.uesp.net/wiki/Skyrim_Mod:Archive_File_Format.
/// </summary>
internal static class Tes5Hashes
{
    /// <summary>
    /// The hash of a folder path, with backslash separators and no trailing separator.
    /// </summary>
    public static ulong Folder(string name) => Hash(name, string.Empty);

    /// <summary>
    /// The hash of a file name (without its folder).
    /// </summary>
    public static ulong File(string name)
    {
        var dot = name.LastIndexOf('.');
        // A trailing dot is no extension, and a leading one makes the whole name the extension, which
        // leaves an empty stem; Wabbajack hashes that as zero, and so does this.
        if (dot < 0 || dot == name.Length - 1)
            return Hash(dot < 0 ? name : name[..dot], string.Empty);
        return Hash(name[..dot], name[dot..]);
    }

    private static ulong Hash(string name, string extension)
    {
        name = name.Replace('/', '\\').ToLowerInvariant();
        extension = extension.ToLowerInvariant();
        if (name.Length == 0) return 0;

        var low = (uint)(byte)name[^1]
            | (uint)(byte)(name.Length < 3 ? '\0' : name[^2]) << 8
            | (uint)(byte)name.Length << 16
            | (uint)(byte)name[0] << 24;
        low |= extension switch
        {
            ".kf" => 0x80u,
            ".nif" => 0x8000u,
            ".dds" => 0x8080u,
            ".wav" => 0x80000000u,
            _ => 0u,
        };

        uint middle = 0;
        for (var i = 1; i < name.Length - 2; i++) middle = middle * 0x1003f + (byte)name[i];

        uint fromExtension = 0;
        foreach (var c in extension) fromExtension = fromExtension * 0x1003f + (byte)c;

        return ((ulong)(middle + fromExtension) << 32) + low;
    }
}
