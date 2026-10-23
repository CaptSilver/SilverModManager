using System.Text;

namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>
/// The Shift_JIS decoder older FromSoft formats store names in. .NET only ships it as a code page provider, so the
/// registration lives here once instead of in every reader.
/// </summary>
public static class ShiftJis
{
    private const int CodePage = 932;

    /// <summary>Code page 932, strict: bytes that are not valid Shift_JIS throw instead of becoming U+FFFD.</summary>
    public static readonly Encoding Encoding = Create();

    private static Encoding Create()
    {
        // Registering twice is harmless, and other code may already have done it.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
