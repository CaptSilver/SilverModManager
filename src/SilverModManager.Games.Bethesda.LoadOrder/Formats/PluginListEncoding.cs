// Follows libloadorder src/load_order/{mod,mutable,textfile_based}.rs (strict_encode, read_plugin_names, read_utf8_plugin_names).
using System.Text;

namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// How the games' plugin lists are encoded. plugins.txt is Windows-1252 in both directions; libloadorder
/// refuses to write a name 1252 can't hold rather than lose a character. loadorder.txt is written as UTF-8 and
/// read as UTF-8, falling back to 1252 for files an older tool wrote.
/// </summary>
public static class PluginListEncoding
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    static PluginListEncoding()
    {
        // .NET ships code page 1252 only through this provider, and it has to be registered before the lookup.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>Windows-1252 that throws on a character it can't encode. Decoding never fails: unused bytes become C1 controls.</summary>
    public static Encoding Windows1252 { get; }

    /// <summary>UTF-8 without a byte order mark that throws on invalid input.</summary>
    public static Encoding StrictUtf8 { get; } = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>Whether the encoding can hold every character of the name without a substitute.</summary>
    public static bool CanEncode(Encoding encoding, string pluginName)
    {
        try
        {
            encoding.GetByteCount(pluginName);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The text of a Windows-1252 file.</summary>
    public static string DecodeWindows1252(ReadOnlySpan<byte> bytes) => Windows1252.GetString(bytes);

    /// <summary>The text of a UTF-8 file without its byte order mark. Throws <see cref="DecoderFallbackException"/> on bytes that aren't UTF-8.</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> bytes) =>
        StrictUtf8.GetString(bytes.StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes);

    /// <summary>The text of a UTF-8 file, or of a Windows-1252 one when the bytes aren't valid UTF-8.</summary>
    public static string DecodeUtf8OrWindows1252(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return DecodeUtf8(bytes);
        }
        catch (DecoderFallbackException)
        {
            return DecodeWindows1252(bytes);
        }
    }
}
