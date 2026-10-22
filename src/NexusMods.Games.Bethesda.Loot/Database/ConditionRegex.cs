using System.Text.RegularExpressions;

namespace NexusMods.Games.Bethesda.Loot.Database;

/// <summary>
/// The regex rules for condition arguments. LOOT compiles them with Rust's regex crate, a
/// linear-time engine with no lookaround and no backreferences; .NET accepts both, so a condition
/// LOOT refuses would otherwise parse here.
/// </summary>
internal static class ConditionRegex
{
    // The interpreter builds its regexes case-insensitively.
    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Names the first lookaround assertion or backreference in <paramref name="pattern"/>, or null if
    /// there is none. Escaped characters and the inside of character classes are not assertions.
    /// </summary>
    public static string? FindUnsupportedConstruct(string pattern)
    {
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '\\')
            {
                // Rust reports \0 as a backreference too: octal escapes are off by default there.
                if (Next(pattern, i + 1) is >= '0' and <= '9') return $"a backreference (\\{pattern[i + 1]})";
                if (Next(pattern, i + 1) == 'k' && Next(pattern, i + 2) is '<' or '\'') return "a named backreference (\\k)";

                i++;
                continue;
            }

            if (inClass)
            {
                if (c == ']') inClass = false;
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

            if (c != '(' || Next(pattern, i + 1) != '?') continue;

            switch (Next(pattern, i + 2))
            {
                case '=': return "a lookahead assertion ((?=)";
                case '!': return "a negative lookahead assertion ((?!)";
                case '<' when Next(pattern, i + 3) == '=': return "a lookbehind assertion ((?<=)";
                case '<' when Next(pattern, i + 3) == '!': return "a negative lookbehind assertion ((?<!)";
            }
        }

        return null;
    }

    private static char Next(string pattern, int index) => index < pattern.Length ? pattern[index] : '\0';
}
