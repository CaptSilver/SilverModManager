namespace SilverModManager.Sdk.Ini;

/// <summary>What a line of an INI file is.</summary>
public enum IniLineKind
{
    /// <summary>Nothing or whitespace only.</summary>
    Blank,

    /// <summary>A line whose first character is <c>;</c>.</summary>
    Comment,

    /// <summary>A <c>[Section]</c> header.</summary>
    Section,

    /// <summary><c>key=value</c>, with an optional <c>;</c> comment after the value.</summary>
    KeyValue,

    /// <summary>Anything else (no <c>=</c>, no brackets). Kept as written and otherwise ignored.</summary>
    Other,
}

/// <summary>
/// One line of an INI file with its terminator. The text is kept as written, so a document that was not edited
/// writes back byte for byte. A key line also keeps what sits around its value (the spacing around <c>=</c>, the
/// trailing comment), so replacing a value changes only the value.
/// </summary>
public sealed class IniLine
{
    private readonly string _prefix;
    private readonly string _suffix;

    private IniLine(IniLineKind kind, string text, string terminator, string name = "", string value = "", string prefix = "", string suffix = "")
    {
        Kind = kind;
        Terminator = terminator;
        Name = name;
        Value = value;
        _prefix = prefix;
        _suffix = suffix;
        Text = kind == IniLineKind.KeyValue ? prefix + value + suffix : text;
    }

    public IniLineKind Kind { get; }

    /// <summary>The line without its terminator.</summary>
    public string Text { get; }

    /// <summary><c>\r\n</c>, <c>\n</c>, or empty for a last line the file did not end.</summary>
    public string Terminator { get; }

    /// <summary>The section name of a <see cref="IniLineKind.Section"/> line, the key of a <see cref="IniLineKind.KeyValue"/> line, else empty.</summary>
    public string Name { get; }

    /// <summary>The value of a key line without surrounding whitespace or the inline comment, else empty.</summary>
    public string Value { get; }

    /// <summary>The same line with another value; the key, spacing and trailing comment stay.</summary>
    internal IniLine WithValue(string value) => new(IniLineKind.KeyValue, "", Terminator, Name, value, _prefix, _suffix);

    internal IniLine WithTerminator(string terminator) =>
        new(Kind, Text, terminator, Name, Value, _prefix, _suffix);

    internal static IniLine NewKey(string key, string value, string terminator) =>
        new(IniLineKind.KeyValue, "", terminator, key, value, key + "=", "");

    internal static IniLine NewSection(string section, string terminator) =>
        new(IniLineKind.Section, $"[{section}]", terminator, section);

    /// <summary>
    /// Reads one line. A <c>;</c> starts an inline comment only at the start of the value or after whitespace, so
    /// a value such as a path or a name that contains a semicolon is not cut.
    /// </summary>
    internal static IniLine Parse(string text, string terminator)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new IniLine(IniLineKind.Blank, text, terminator);
        if (trimmed[0] == ';') return new IniLine(IniLineKind.Comment, text, terminator);

        if (trimmed[0] == '[' && trimmed.IndexOf(']') is var close and > 0)
        {
            var after = trimmed[(close + 1)..].TrimStart();
            if (after.Length == 0 || after[0] == ';')
                return new IniLine(IniLineKind.Section, text, terminator, trimmed[1..close].Trim());
        }

        var equals = text.IndexOf('=');
        if (equals < 0 || text[..equals].Trim().Length == 0) return new IniLine(IniLineKind.Other, text, terminator);

        var key = text[..equals].Trim();
        var rest = text[(equals + 1)..];
        var commentAt = CommentStart(rest);
        var valuePart = rest[..commentAt];
        var value = valuePart.Trim();
        var leading = valuePart.Length - valuePart.TrimStart().Length;
        return new IniLine(IniLineKind.KeyValue, text, terminator, key, value,
            text[..(equals + 1)] + rest[..leading], rest[(leading + value.Length)..]);
    }

    private static int CommentStart(string rest)
    {
        for (var i = 0; i < rest.Length; i++)
            if (rest[i] == ';' && (i == 0 || char.IsWhiteSpace(rest[i - 1]))) return i;
        return rest.Length;
    }
}
