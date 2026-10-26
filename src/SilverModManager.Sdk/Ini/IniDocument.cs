using System.Text;

namespace SilverModManager.Sdk.Ini;

/// <summary>
/// An INI file as an ordered list of lines. Reading and writing it back without a change gives the same bytes
/// (encoding, line endings, comments, spacing, duplicate keys and all); an edit touches only the line it is about.
/// Section and key names ignore case, like the games' own readers. Keys before the first header belong to the
/// unnamed section <c>""</c>. A key that occurs twice is read and edited at its first occurrence.
/// </summary>
public sealed class IniDocument
{
    private const string CrLf = "\r\n";
    private readonly List<IniLine> _lines;

    private IniDocument(List<IniLine> lines, IniEncoding encoding, string newLine)
    {
        _lines = lines;
        Encoding = encoding;
        NewLine = newLine;
    }

    public IniEncoding Encoding { get; }

    /// <summary>The ending new lines are written with: the file's first one, or CRLF when it has none.</summary>
    public string NewLine { get; }

    public IReadOnlyList<IniLine> Lines => _lines;

    /// <summary>The section names in the order they first appear, without the unnamed section.</summary>
    public IEnumerable<string> Sections =>
        _lines.Where(l => l.Kind == IniLineKind.Section).Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>A document with no lines, written as UTF-8 with CRLF.</summary>
    public static IniDocument CreateEmpty() => new([], IniEncoding.Utf8, CrLf);

    /// <summary>Reads a file's bytes. Any bytes are accepted; see <see cref="IniEncoding.Latin1"/>.</summary>
    public static IniDocument Parse(byte[] bytes)
    {
        var (encoding, text) = IniEncodings.Decode(bytes);
        return Parse(text, encoding);
    }

    /// <summary>Reads text that was already decoded; it writes back as UTF-8.</summary>
    public static IniDocument Parse(string text) =>
        text.StartsWith('﻿') ? Parse(text[1..], IniEncoding.Utf8Bom) : Parse(text, IniEncoding.Utf8);

    private static IniDocument Parse(string text, IniEncoding encoding)
    {
        var lines = new List<IniLine>();
        string? newLine = null;
        for (var start = 0; start < text.Length;)
        {
            var feed = text.IndexOf('\n', start);
            var end = feed < 0 ? text.Length : feed + 1;
            var terminatorLength = feed < 0 ? 0 : (feed > start && text[feed - 1] == '\r' ? 2 : 1);
            var terminator = text[(end - terminatorLength)..end];
            if (terminatorLength > 0) newLine ??= terminator;
            lines.Add(IniLine.Parse(text[start..(end - terminatorLength)], terminator));
            start = end;
        }

        return new IniDocument(lines, encoding, newLine ?? CrLf);
    }

    /// <summary>The file's bytes, with the mark its encoding calls for.</summary>
    /// <exception cref="IniEncodingException">A setting holds a character the file's encoding cannot represent.</exception>
    public byte[] ToBytes()
    {
        var encoding = IniEncodings.Strict(Encoding);
        try
        {
            return [.. IniEncodings.Preamble(Encoding), .. encoding.GetBytes(ToText())];
        }
        catch (EncoderFallbackException)
        {
            var offender = _lines.First(l => !Encodes(encoding, l.Text));
            throw new IniEncodingException(offender.Kind == IniLineKind.KeyValue ? offender.Name : offender.Text.Trim(), Encoding);
        }
    }

    /// <summary>The file's text without any byte order mark.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach (var line in _lines) text.Append(line.Text).Append(line.Terminator);
        return text.ToString();
    }

    /// <summary>The value of <paramref name="key"/> in <paramref name="section"/>, or null when it is not set.</summary>
    public string? Get(string section, string key) =>
        FindKey(section, key) is var at and >= 0 ? _lines[at].Value : null;

    /// <summary>Every key of <paramref name="section"/>, duplicates included, in file order.</summary>
    public IEnumerable<KeyValuePair<string, string>> Entries(string section) =>
        SectionOfEachLine().Where(e => e.Line.Kind == IniLineKind.KeyValue && Same(e.Section, section))
            .Select(e => KeyValuePair.Create(e.Line.Name, e.Line.Value));

    /// <summary>
    /// Sets a key's value in place, keeping its position and any inline comment. A new key goes at the end of the
    /// section, before the blank lines that separate it from the next one; a missing section is added at the end.
    /// </summary>
    public void Set(string section, string key, string value)
    {
        CheckName(section, "section", forSection: true);
        CheckName(key, "key", forSection: false);
        if (value.Contains('\n') || value.Contains('\r')) throw new ArgumentException("A value cannot span lines.", nameof(value));

        if (FindKey(section, key) is var at and >= 0)
        {
            _lines[at] = _lines[at].WithValue(value);
            return;
        }

        if (!TryGetBlock(section, out var block))
        {
            AppendSection(section, [KeyValuePair.Create(key, value)]);
            return;
        }

        Insert(AfterContent(block), [IniLine.NewKey(key, value, NewLine)]);
    }

    /// <summary>Removes the first line that sets <paramref name="key"/>. False when there was none.</summary>
    public bool Remove(string section, string key)
    {
        if (FindKey(section, key) is not (var at and >= 0)) return false;
        _lines.RemoveAt(at);
        return true;
    }

    /// <summary>
    /// Replaces every key of <paramref name="section"/> that <paramref name="isReplaced"/> accepts with the given
    /// lines. They take the place of the first replaced line, or follow the section's last content line when none
    /// matched; other lines of the section stay. A missing section is added at the end even for an empty list.
    /// Only the section's first block is looked at.
    /// </summary>
    public void ReplaceKeys(string section, Func<string, bool> isReplaced, IReadOnlyList<KeyValuePair<string, string>> replacement)
    {
        CheckName(section, "section", forSection: true);
        foreach (var (key, value) in replacement)
        {
            CheckName(key, "key", forSection: false);
            if (value.Contains('\n') || value.Contains('\r')) throw new ArgumentException("A value cannot span lines.", nameof(replacement));
        }

        if (!TryGetBlock(section, out var block))
        {
            AppendSection(section, replacement);
            return;
        }

        var matched = Enumerable.Range(block.Start, block.End - block.Start)
            .Where(i => _lines[i].Kind == IniLineKind.KeyValue && isReplaced(_lines[i].Name))
            .ToList();
        var insertAt = matched.Count > 0 ? matched[0] : AfterContent(block);
        for (var i = matched.Count - 1; i >= 0; i--) _lines.RemoveAt(matched[i]);

        Insert(insertAt, replacement.Select(e => IniLine.NewKey(e.Key, e.Value, NewLine)).ToList());
    }

    /// <summary>
    /// Applies every <c>key=value</c> the tweak sets under a section header to this document. Keys the tweak has
    /// before its first header have no section to land in and are skipped.
    /// </summary>
    public void Merge(IniDocument tweak)
    {
        foreach (var section in tweak.Sections)
            foreach (var (key, value) in tweak.Entries(section))
                Set(section, key, value);
    }

    private IEnumerable<(string Section, IniLine Line)> SectionOfEachLine()
    {
        var section = "";
        foreach (var line in _lines)
        {
            if (line.Kind == IniLineKind.Section) section = line.Name;
            yield return (section, line);
        }
    }

    private int FindKey(string section, string key)
    {
        var current = "";
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Kind == IniLineKind.Section) current = line.Name;
            else if (line.Kind == IniLineKind.KeyValue && Same(current, section) && Same(line.Name, key)) return i;
        }

        return -1;
    }

    /// <summary>The line range after a section's header (from 0 for the unnamed section) up to the next header.</summary>
    private bool TryGetBlock(string section, out (int Start, int End) block)
    {
        var start = section.Length == 0 ? 0 : _lines.FindIndex(l => l.Kind == IniLineKind.Section && Same(l.Name, section)) + 1;
        if (start == 0 && section.Length != 0)
        {
            block = default;
            return false;
        }

        var end = _lines.FindIndex(start, l => l.Kind == IniLineKind.Section);
        block = (start, end < 0 ? _lines.Count : end);
        return true;
    }

    /// <summary>Where a line goes to join the block: after its last line that is not blank.</summary>
    private int AfterContent((int Start, int End) block)
    {
        var at = block.End;
        while (at > block.Start && _lines[at - 1].Kind == IniLineKind.Blank) at--;
        return at;
    }

    private void Insert(int at, IReadOnlyList<IniLine> lines)
    {
        // A last line the file left unterminated needs its ending before another line can follow it.
        if (lines.Count > 0 && at > 0 && _lines[at - 1].Terminator.Length == 0)
            _lines[at - 1] = _lines[at - 1].WithTerminator(NewLine);
        _lines.InsertRange(at, lines);
    }

    private void AppendSection(string section, IReadOnlyList<KeyValuePair<string, string>> keys)
    {
        var lines = new List<IniLine>();
        if (section.Length != 0) lines.Add(IniLine.NewSection(section, NewLine));
        lines.AddRange(keys.Select(e => IniLine.NewKey(e.Key, e.Value, NewLine)));
        Insert(_lines.Count, lines);
    }

    private static bool Encodes(Encoding encoding, string text)
    {
        try
        {
            encoding.GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static void CheckName(string name, string what, bool forSection)
    {
        var bad = name.Contains('\n') || name.Contains('\r') || (forSection ? name.Contains(']') : name.Contains('=') || name.Length == 0);
        if (bad) throw new ArgumentException($"\"{name}\" is not a valid {what} name.", nameof(name));
    }
}
