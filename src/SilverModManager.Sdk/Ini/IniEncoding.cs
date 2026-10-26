using System.Text;

namespace SilverModManager.Sdk.Ini;

/// <summary>
/// How an INI file is stored. The game's own tools write Windows-1252 or UTF-8, some tweak authors save UTF-16;
/// a document writes back in whatever it was read as so an edit never changes the file's encoding.
/// </summary>
public enum IniEncoding
{
    /// <summary>UTF-8 without a byte order mark. What a file with no mark and no invalid sequence is read as.</summary>
    Utf8,

    /// <summary>UTF-8 with the EF BB BF mark.</summary>
    Utf8Bom,

    /// <summary>UTF-16 little endian with the FF FE mark.</summary>
    Utf16LittleEndian,

    /// <summary>UTF-16 big endian with the FE FF mark.</summary>
    Utf16BigEndian,

    /// <summary>
    /// One byte per character, no mark. A file with bytes that are not valid UTF-8 is read this way because the
    /// mapping is 1:1, so any bytes round trip. Windows-1252 text (the game's usual encoding) reads correctly
    /// for everything except the few characters 0x80-0x9F holds.
    /// </summary>
    Latin1,

    /// <summary>
    /// The UTF-8 mark followed by bytes that are not UTF-8, as when a tool writes Windows-1252 into a file an
    /// editor saved with the mark. Read and written like <see cref="Latin1"/>, with the mark kept.
    /// </summary>
    Latin1WithUtf8Mark,
}

internal static class IniEncodings
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] Preamble(IniEncoding encoding) => encoding switch
    {
        IniEncoding.Utf8Bom or IniEncoding.Latin1WithUtf8Mark => [0xEF, 0xBB, 0xBF],
        IniEncoding.Utf16LittleEndian => [0xFF, 0xFE],
        IniEncoding.Utf16BigEndian => [0xFE, 0xFF],
        _ => [],
    };

    /// <summary>
    /// An encoder that throws on a character the encoding cannot hold. The default Latin1 encoder would turn it
    /// into a question mark and the file would be corrupted without anyone being told.
    /// </summary>
    public static Encoding Strict(IniEncoding encoding) => encoding switch
    {
        IniEncoding.Utf8 or IniEncoding.Utf8Bom => StrictUtf8,
        IniEncoding.Utf16LittleEndian => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
        IniEncoding.Utf16BigEndian => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
        _ => Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
    };

    /// <summary>Picks the encoding of <paramref name="bytes"/> and decodes the text after any mark.</summary>
    public static (IniEncoding Encoding, string Text) Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            try
            {
                return (IniEncoding.Utf8Bom, StrictUtf8.GetString(bytes, 3, bytes.Length - 3));
            }
            catch (DecoderFallbackException)
            {
                return (IniEncoding.Latin1WithUtf8Mark, Encoding.Latin1.GetString(bytes, 3, bytes.Length - 3));
            }
        }
        if (bytes is [0xFF, 0xFE, ..])
            return (IniEncoding.Utf16LittleEndian, Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        if (bytes is [0xFE, 0xFF, ..])
            return (IniEncoding.Utf16BigEndian, Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));

        try
        {
            return (IniEncoding.Utf8, StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return (IniEncoding.Latin1, Encoding.Latin1.GetString(bytes));
        }
    }
}
