using System.IO.Hashing;
using System.Text;
using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// The hashes a BA2 entry records: the CRC32 of the lower-cased directory path and of the lower-cased file
/// name without its extension, with backslash separators. A modlist records them per entry and the builder
/// writes the recorded values; this is for fixtures and for entries that have none.
/// </summary>
internal static class Ba2Hashes
{
    public static uint Directory(string path) => Crc(path.Replace('/', '\\'));

    public static uint Name(string stem) => Crc(stem);

    /// <summary>
    /// The directory hash, name hash and four character extension field ("dds\0") of a path.
    /// </summary>
    public static (uint Dir, uint Name, string Extension) Of(RelativePath path)
    {
        var text = path.ToString().Replace('/', '\\');
        var slash = text.LastIndexOf('\\');
        var directory = slash < 0 ? string.Empty : text[..slash];
        var file = text[(slash + 1)..];

        var dot = file.LastIndexOf('.');
        var stem = dot < 0 ? file : file[..dot];
        var extension = dot < 0 ? string.Empty : file[(dot + 1)..];
        return (Directory(directory), Name(stem), extension.PadRight(4, '\0')[..4]);
    }

    private static uint Crc(string text) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
}
