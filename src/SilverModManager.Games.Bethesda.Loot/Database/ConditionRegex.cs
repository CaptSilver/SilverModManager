using System.Text.RegularExpressions;

namespace NexusMods.Games.Bethesda.Loot.Database;

/// <summary>
/// The regex rules for condition arguments. LOOT compiles them with Rust's regex crate, a
/// linear-time engine with no lookaround, no backreferences and a stricter syntax than .NET's. Some
/// of its syntax also means something else in .NET. Either way a condition would otherwise parse
/// here and then match differently from LOOT.
/// </summary>
internal static class ConditionRegex
{
    // The interpreter builds its regexes case-insensitively.
    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Describes the first construct in <paramref name="pattern"/> that Rust's regex crate refuses or
    /// reads differently from .NET, or returns null if there is none. This is a scan for the constructs
    /// .NET would accept, not a Rust parser. Rust refuses lookaround, backreferences, group forms only
    /// .NET has, escapes it does not define and malformed repetitions. It reads nested classes, class
    /// set operations and the \&lt; and \&gt; word boundaries differently. An escaped character never
    /// starts a construct, and a group or repetition inside a character class is a literal.
    /// </summary>
    public static string? FindUnsupportedConstruct(string pattern)
    {
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '\\')
            {
                if (FindUnsupportedEscape(pattern, i, inClass) is { } escape) return escape;

                // \p{L}, \x{41} and the like carry a braced name that is not a repetition.
                i += Next(pattern, i + 1) is 'p' or 'P' or 'x' or 'u' or 'U' && Next(pattern, i + 2) == '{'
                    ? Math.Max(pattern.IndexOf('}', i + 2) - i, 1)
                    : 1;
                continue;
            }

            if (inClass)
            {
                // .NET reads [ as a literal in a class and [a-z-[b]] as subtraction; Rust nests (and reads
                // [:alpha:] as a POSIX class) and has its own set operations.
                if (c == '[') return ReadDifferently("a nested character class ([)");
                if (c == ']') inClass = false;
                else if (c is '&' or '-' or '~' && Next(pattern, i + 1) == c) return ReadDifferently($"a character class set operation ({c}{c})");
                continue;
            }

            if (c == '[')
            {
                inClass = true;

                // A ] straight after the opening bracket, or after its ^, is a member of the class.
                if (Next(pattern, i + 1) == '^') i++;
                if (Next(pattern, i + 1) == ']') i++;
                continue;
            }

            // .NET takes a { that is not a quantifier as a literal; Rust rejects it.
            if (c == '{')
            {
                var close = RepetitionEnd(pattern, i);
                if (close < 0) return Refused("a malformed repetition ({)");

                i = close;
                continue;
            }

            if (c != '(' || Next(pattern, i + 1) != '?') continue;

            switch (Next(pattern, i + 2))
            {
                case '=': return Refused("a lookahead assertion ((?=)");
                case '!': return Refused("a negative lookahead assertion ((?!)");
                case '<' when Next(pattern, i + 3) == '=': return Refused("a lookbehind assertion ((?<=)");
                case '<' when Next(pattern, i + 3) == '!': return Refused("a negative lookbehind assertion ((?<!)");
                case '>': return Refused("an atomic group ((?>)");
                case '(': return Refused("a conditional group ((?()");
                case '#': return Refused("an inline comment ((?#)");
                case 'P' when Next(pattern, i + 3) != '<': return Refused("an unsupported group form ((?P)");
                case '\'': return Refused("an unsupported group form ((?')");
            }
        }

        return null;
    }

    private static string? FindUnsupportedEscape(string pattern, int backslash, bool inClass)
    {
        var escaped = Next(pattern, backslash + 1);

        // Rust reports \0 as a backreference too: octal escapes are off by default there.
        if (escaped is >= '0' and <= '9') return Refused($"a backreference (\\{escaped})");
        if (escaped == 'k' && Next(pattern, backslash + 2) is '<' or '\'') return Refused("a named backreference (\\k)");

        // Rust reads \< and \> as word boundaries, which are not allowed in a class; .NET reads a literal.
        if (escaped is '<' or '>')
            return inClass ? Refused($"an unsupported escape sequence (\\{escaped})") : ReadDifferently($"a word boundary (\\{escaped})");

        // Rust escapes are the shorthand classes, the control characters, \x \u \U \p \P, and (outside
        // a class) the anchors and word boundaries. Any other letter is an error there; .NET has \G, \Z, \e and more.
        var supported = inClass ? "dDsSwWtnrfvaxuUpP" : "dDsSwWtnrfvaxuUpPbBAz";
        if (escaped is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') && !supported.Contains(escaped))
            return Refused($"an unsupported escape sequence (\\{escaped})");

        return escaped > 127 ? Refused("an escaped non-ASCII character") : null;
    }

    private static string Refused(string construct) => $"{construct}, which LOOT's regex engine does not support";

    private static string ReadDifferently(string construct) => $"{construct}, which LOOT's regex engine reads differently from .NET";

    /// <summary>The index of the closing brace of a valid {n}, {n,} or {n,m} starting at <paramref name="open"/>, or -1.</summary>
    private static int RepetitionEnd(string pattern, int open)
    {
        var i = open + 1;
        var digits = i;
        while (Next(pattern, i) is >= '0' and <= '9') i++;
        if (i == digits) return -1;

        if (Next(pattern, i) == ',')
        {
            i++;
            while (Next(pattern, i) is >= '0' and <= '9') i++;
        }

        return Next(pattern, i) == '}' ? i : -1;
    }

    private static char Next(string pattern, int index) => index < pattern.Length ? pattern[index] : '\0';
}
