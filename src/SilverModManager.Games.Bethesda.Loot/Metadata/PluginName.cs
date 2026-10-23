using System.Text;
using System.Text.RegularExpressions;

namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// A plugin name in metadata: either a literal filename or, if it contains any of <c>:\*?|</c>, a
/// regular expression matched against the whole plugin filename. Follows libloot's
/// metadata/plugin_metadata.rs.
/// </summary>
/// <remarks>
/// Equality is on the name text, case-sensitively, like libloot's derived comparison. Use
/// <see cref="Matches"/> for the case-insensitive match against a plugin file.
/// </remarks>
public sealed class PluginName : IEquatable<PluginName>
{
    private const string GhostFileExtension = ".ghost";

    private readonly Regex? _regex;

    /// <exception cref="InvalidPluginNameException">The name is a regex that does not compile.</exception>
    public PluginName(string name)
    {
        Value = TrimDotGhost(name);
        if (!IsRegexName(Value)) return;

        // libloot compiles ^{name}$ with regress in "iu" mode, an ECMAScript engine. The anchors
        // wrap the name as written, so ^a|b$ means (^a)|(b$), not ^(a|b)$. .NET's ECMAScript mode
        // is close but not identical: it accepts some escapes and brackets that regress's unicode
        // mode rejects, so a pattern libloot refuses can compile here.
        try
        {
            _regex = new Regex($"^{ReplaceCapturingGroups(Value)}$", RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
        }
        catch (ArgumentException e)
        {
            throw new InvalidPluginNameException(Value, e);
        }
    }

    public string Value { get; }

    public bool IsRegex => _regex is not null;

    /// <summary>
    /// Whether <paramref name="otherName"/>, which must be a literal filename, is matched by this
    /// name.
    /// </summary>
    public bool Matches(string otherName)
    {
        return _regex is not null
            ? _regex.IsMatch(otherName)
            : string.Equals(Value, otherName, StringComparison.OrdinalIgnoreCase);
    }

    public bool Equals(PluginName? other) => other is not null && Value == other.Value;

    public override bool Equals(object? obj) => Equals(obj as PluginName);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    /// <summary>
    /// Removes a trailing <c>.ghost</c>, ASCII case-insensitively, as libloot does for ghosted plugins.
    /// </summary>
    internal static string TrimDotGhost(string name)
    {
        if (name.Length < GhostFileExtension.Length) return name;

        var start = name.Length - GhostFileExtension.Length;
        for (var i = 0; i < GhostFileExtension.Length; i++)
        {
            var c = name[start + i];
            if (c is >= 'A' and <= 'Z') c = (char)(c + ('a' - 'A'));
            if (c != GhostFileExtension[i]) return name;
        }

        return name[..start];
    }

    internal static bool IsRegexName(string name) => name.AsSpan().IndexOfAny(@":\*?|") >= 0;

    /// <summary>
    /// Turns every capturing group <c>(</c> into a non-capturing <c>(?:</c>, leaving escaped
    /// parentheses and groups that already start with <c>?</c> alone.
    /// </summary>
    internal static string ReplaceCapturingGroups(string regex)
    {
        // Pieces end in '(' except possibly the last, as with Rust's split_inclusive('(').
        var result = new StringBuilder(regex.Length + 8);
        var start = 0;
        while (start < regex.Length)
        {
            var open = regex.IndexOf('(', start);
            var end = open < 0 ? regex.Length : open + 1;
            var before = regex.AsSpan(start, end - start);
            result.Append(before);
            start = end;

            if (start >= regex.Length) break;

            var startsGroupModifier = regex[start] == '?';
            var escaped = before.EndsWith(@"\(") && !before.EndsWith(@"\\(");
            if (!startsGroupModifier && !escaped) result.Append("?:");
        }

        return result.ToString();
    }
}
