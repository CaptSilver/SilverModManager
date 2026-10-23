using System.Text;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// Reads the null-terminated UTF-16LE strings that binder member names, FMG entries and TPF texture names share,
/// along with their one hazard: a buffer that ends mid-string.
/// </summary>
internal static class Utf16Strings
{
    // Strict, so a lone surrogate throws instead of turning into U+FFFD.
    private static readonly UnicodeEncoding Strict = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes the string starting at <paramref name="offset"/>. Each reader passes its own exception type, so a
    /// failure reads as that format's.
    /// </summary>
    /// <param name="error">Builds the exception to throw from a description of what is wrong.</param>
    public static string ReadZ(ReadOnlySpan<byte> data, int offset, Func<string, Exception> error)
    {
        var end = offset;
        while (true)
        {
            if (end + 1 >= data.Length)
                throw error("unterminated string");
            if (data[end] == 0 && data[end + 1] == 0)
                break;
            end += 2;
        }

        try
        {
            return Strict.GetString(data[offset..end]);
        }
        catch (ArgumentException e)
        {
            throw error($"string at {offset} is not valid UTF-16: {e.Message}");
        }
    }
}
