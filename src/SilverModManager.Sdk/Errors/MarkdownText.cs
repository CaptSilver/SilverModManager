using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Errors;

/// <summary>
/// Puts text the app did not write (mod, collection, file and author names from Nexus or collection data, archive
/// entry names) into Markdown without letting it become Markdown. Every message and dialog body that interpolates
/// such a name goes through here, because the renderer opens links, loads images and draws emphasis.
/// </summary>
[PublicAPI]
public static partial class MarkdownText
{
    // The renderer unescapes a backslash before these four and shows the backslash before any other punctuation,
    // so they are the only ones escaped that way.
    private const string BackslashEscaped = "\\*_~";
    private const string BlockMarkers = "#+->";
    private const char ZeroWidthSpace = '\u200B';

    // MODIFIER LETTER GRAVE ACCENT: looks like a backtick and is not one.
    private const char BacktickLookalike = '\u02CB';

    /// <summary>
    /// Plain text for a sentence: line breaks and tabs become spaces, other control and format characters (bidi
    /// overrides, zero-width marks) are dropped, emphasis markers get a backslash, and a zero-width space after each
    /// <c>]</c> keeps <c>[text](target)</c> from ever being a link or image. A block marker at the start (heading,
    /// list, quote) is defused the same way. A backtick becomes U+02CB, because the renderer pairs backticks over the
    /// whole paragraph: a stray one here would pair with the fence of a later <see cref="Code(string?)"/> and leave
    /// that span's text outside, as live Markdown. A trailing backslash gets a zero-width space so it cannot hide a
    /// following span either. Angle brackets and the rest are left alone: the renderer shows raw HTML as text, makes
    /// no autolinks and no reference links. For a name, prefer
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
            builder.Append(c == '`' ? BacktickLookalike : c);
            if (c == ']') builder.Append(ZeroWidthSpace);
        }
        // The renderer does not open a code span right after a backslash, escaped or not.
        if (clean.Length > 0 && clean[^1] == '\\') builder.Append(ZeroWidthSpace);
        return builder.ToString();
    }

    /// <summary>
    /// The message of <paramref name="exception"/> for a sentence. A refusal's message is Markdown already and stays
    /// as it is; escaping it again would show the backslashes. Anything else (an I/O error, a parser message) is
    /// outside text and goes through <see cref="Inline(string?)"/>.
    /// </summary>
    public static string Message(Exception exception) => exception is UserFacingException ? exception.Message : Inline(exception.Message);

    /// <summary>
    /// <see cref="Inline(string?)"/> for a finished sentence that already has code spans, such as a rendered log
    /// message that quotes names in backticks. Spans are kept and shown exactly; only the text between them is escaped.
    /// </summary>
    public static string InlineKeepingCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length + 8);
        var position = 0;
        foreach (var span in CodeSpan().EnumerateMatches(text))
        {
            builder.Append(Inline(text[position..span.Index]));
            builder.Append(Sanitise(text.Substring(span.Index, span.Length)));
            position = span.Index + span.Length;
        }
        builder.Append(Inline(text[position..]));
        return builder.ToString();
    }

    /// <summary>
    /// The text of a Markdown message for a place that does not render Markdown (a toast, the command line, a plain
    /// text block): code spans show their content, and the escapes <see cref="Inline(string?)"/> added are removed.
    /// Backticks that <see cref="Inline(string?)"/> replaced stay replaced.
    /// </summary>
    public static string ToPlain(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;

        return PlainPart().Replace(markdown, match =>
        {
            if (match.Groups["escaped"].Success) return match.Groups["escaped"].Value;
            if (match.Groups["code"].Success)
            {
                var code = match.Groups["code"].Value;
                // The renderer strips one space from each end when both are there.
                return code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0 ? code[1..^1] : code;
            }
            return string.Empty;
        });
    }

    // The renderer's own pattern for a code span, which it applies to a whole paragraph before it looks for links.
    [GeneratedRegex(@"(?<!\\)(`+)(.+?)(?<!`)\1(?!`)", RegexOptions.Singleline)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"(?<!\\)(`+)(?<code>.+?)(?<!`)\1(?!`)|\\(?<escaped>[\\*_~])|\u200B", RegexOptions.Singleline)]
    private static partial Regex PlainPart();

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
