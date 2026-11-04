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
    public static string Inline(string? text) => Escape(text, startsLine: true);

    /// <summary>
    /// <see cref="Inline(string?)"/> for text that sits in the middle of a line, such as a version after a plugin name.
    /// Nothing there can start a heading, list or quote, so a start like <c>1.27.4</c> is left as it is instead of getting
    /// an invisible character in front.
    /// </summary>
    public static string InlineMidLine(string? text) => Escape(text, startsLine: false);

    private static string Escape(string? text, bool startsLine)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var clean = Sanitise(text);
        var builder = new StringBuilder(clean.Length + 8);
        if (startsLine && clean.Length > 0 && StartsBlock(clean)) builder.Append(ZeroWidthSpace);
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

    /// <summary>
    /// Text for the inside of a code span or fence the surrounding template already opened. Nothing in a span is
    /// interpreted, so only what could end it is changed: a backtick becomes U+02CB, and control and format characters
    /// and line breaks go as in <see cref="PlainText(string?, bool)"/>. Use <see cref="Code(string?)"/> where the text
    /// brings its own fence.
    /// </summary>
    public static string InsideCode(string? text) => PlainText(text).Replace('`', BacktickLookalike);

    /// <summary><see cref="Code(string?)"/> for any value, such as a path or an id, through its <c>ToString()</c>.</summary>
    public static string Code(object? value) => Code(value?.ToString());

    /// <summary>
    /// A fenced code block that the text cannot close: the fence is at least three backticks and longer than any run
    /// inside. Line breaks are kept, other control characters are dropped.
    /// </summary>
    public static string CodeBlock(string? text)
    {
        var body = new StringBuilder();
        // By rune, so a format character above U+FFFF (a tag character, say) is dropped like PlainText drops it.
        foreach (var rune in (text ?? string.Empty).ReplaceLineEndings("\n").EnumerateRunes())
        {
            if (rune.Value == '\n' || (!Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) is not UnicodeCategory.Format)) body.Append(rune.ToString());
        }

        var clean = body.ToString();
        var fence = new string('`', Math.Max(3, LongestBacktickRun(clean) + 1));
        return $"{fence}\n{clean}\n{fence}";
    }

    private static string Sanitise(string text) => PlainText(text);

    /// <summary>
    /// <paramref name="text"/> with everything dropped that a terminal or a text box would act on instead of show:
    /// C0 and C1 controls (escape sequences, bell, U+009B) and Unicode format characters (bidi overrides, zero-width
    /// marks), including those above U+FFFF, and unpaired surrogates. Line breaks and tabs become spaces, or with
    /// <paramref name="keepLayout"/> line feeds and tabs stay, for text whose own template has line structure; a carriage
    /// return is always dropped, so a value cannot move the cursor. Markup is not touched.
    /// </summary>
    public static string PlainText(string? text, bool keepLayout = false)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            // Unpaired surrogates enumerate as U+FFFD; the replacement character is only kept when the input had it.
            if (rune.Value == 0xFFFD && !text.Contains('\uFFFD')) continue;
            if (rune.Value is '\n' or '\t' && keepLayout) builder.Append((char)rune.Value);
            else if (rune.Value is '\r' or '\n' or '\t' or '\v' or '\f' or 0x85 or 0x2028 or 0x2029) builder.Append(keepLayout ? string.Empty : " ");
            else if (Rune.IsControl(rune)) continue;
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format) continue;
            else builder.Append(rune.ToString());
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
