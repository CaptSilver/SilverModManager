using System.Text;

namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// The name decodings Wabbajack uses per archive version. Modlists record member paths as Wabbajack decoded
/// them, so these follow it even where it is wrong for the game (UTF-7 for version 103 drops characters after
/// a '+'; ASCII for Morrowind turns other bytes into '?'): a lookup by recorded path must find the same string.
/// </summary>
internal static class ArchiveEncodings
{
    public static readonly Encoding Ascii = Encoding.ASCII;

#pragma warning disable SYSLIB0001 // obsolete, but it is what the recorded paths were decoded with
    public static readonly Encoding Utf7 = Encoding.UTF7;
#pragma warning restore SYSLIB0001

    public static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? throw new InvalidOperationException("The code pages provider has no Windows-1252 encoding");

    public static Encoding ForTes5Version(uint version) => version switch
    {
        103 => Utf7,
        104 => Utf8,
        105 => Windows1252,
        _ => throw new ArgumentOutOfRangeException(nameof(version), version, "not a TES5 archive version"),
    };
}
