using System.Globalization;
using System.Text;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Errors;

/// <summary>
/// Puts text the app did not write (mod, collection, file and author names from Nexus or collection data, archive
/// entry names) into Markdown without letting it become Markdown. Every message and dialog body that interpolates
/// such a name goes through here, because the renderer opens links, loads images and draws emphasis.
/// </summary>
[PublicAPI]
public static class MarkdownText
{
    // The renderer unescapes a backslash before these four and shows the backslash before any other punctuation,
    // so they are the only ones escaped that way.
    private const string BackslashEscaped = "\\*_~";
    private const string BlockMarkers = "#+->`";
    private const char ZeroWidthSpace = '\u200B';

    /// <summary>
    /// Plain text for a sentence: line breaks and tabs become spaces, other control and format characters (bidi
    /// overrides, zero-width marks) are dropped, emphasis markers get a backslash, and a zero-width space after each
    /// <c>]</c> keeps <c>[text](target)</c> from ever being a link or image. A block marker at the start (heading,
    /// list, quote, fence) is defused the same way. Backticks, angle brackets and the rest are left alone: the
    /// renderer shows raw HTML as text, makes no autolinks and no reference links. For a name, prefer
    /// <see cref="Code(string?)"/>, which shows it exactly.
    /// </summary>
    public static string Inline(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var clean = Sanitise(text);
        var builder = new StringBuilder(clean.Length + 8);
        if (clean.Length > 0 && StartsBlock(clean)) builder.Append(ZeroWidthSpace);
        foreach (var c in clean)
        {
            if (BackslashEscaped.Contains(c)) builder.Append('\\');
            builder.Append(c);
            if (c == ']') builder.Append(ZeroWidthSpace);
        }
        return builder.ToString();
    }

    private static bool StartsBlock(string clean)
    {
        if (BlockMarkers.Contains(clean[0])) return true;
        var digits = 0;
        while (digits < clean.Length && char.IsAsciiDigit(clean[digits])) digits++;
        return digits > 0 && digits < clean.Length && clean[digits] is '.' or ')';
    }

    /// <summary><see cref="Inline(string?)"/> for any value, such as a path or an id, through its <c>ToString()</c>.</summary>
    public static string Inline(object? value) => Inline(value?.ToString());

    /// <summary>
    /// An inline code span. The fence is one backtick longer than the longest run inside, so the text cannot close the
    /// span; nothing inside a span is interpreted, which keeps the text readable (paths, file names).
    /// </summary>
    public static string Code(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var clean = Sanitise(text);
        var fence = new string('`', LongestBacktickRun(clean) + 1);
        // A backtick at either end would merge with the fence; one space on both sides is stripped by the renderer.
        var pad = clean[0] == '`' || clean[^1] == '`' ? " " : string.Empty;
        return $"{fence}{pad}{clean}{pad}{fence}";
    }

    /// <summary><see cref="Code(string?)"/> for any value, such as a path or an id, through its <c>ToString()</c>.</summary>
    public static string Code(object? value) => Code(value?.ToString());

    /// <summary>
    /// A fenced code block that the text cannot close: the fence is at least three backticks and longer than any run
    /// inside. Line breaks are kept, other control characters are dropped.
    /// </summary>
    public static string CodeBlock(string? text)
    {
        var body = new StringBuilder();
        foreach (var c in (text ?? string.Empty).ReplaceLineEndings("\n"))
        {
            if (c == '\n' || (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) is not UnicodeCategory.Format)) body.Append(c);
        }

        var clean = body.ToString();
        var fence = new string('`', Math.Max(3, LongestBacktickRun(clean) + 1));
        return $"{fence}\n{clean}\n{fence}";
    }

    private static string Sanitise(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029') builder.Append(' ');
            else if (char.IsControl(c)) continue;
            else if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format) continue;
            else builder.Append(c);
        }
        return builder.ToString();
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }
        return longest;
    }
}
