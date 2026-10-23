using System.Text;

namespace NexusMods.Wabbajack.Mapping;

/// <summary>A tool MO2 lists in its toolbar. Its working directory is not kept: the app launches tools from their own folder.</summary>
public readonly record struct Mo2Executable(string Title, string Binary, string Arguments);

/// <summary>The sections of a <c>ModOrganizer.ini</c>, with MO2's (Qt's) escaping already removed from the values.</summary>
public sealed class Mo2IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    internal void Set(string section, string key, string value)
    {
        if (!_sections.TryGetValue(section, out var keys))
            _sections[section] = keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        keys[key] = value;
    }

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var keys) && keys.TryGetValue(key, out var value) ? value : null;

    public IReadOnlyDictionary<string, string> Section(string section) =>
        _sections.TryGetValue(section, out var keys) ? keys : new Dictionary<string, string>();
}

/// <summary>Reads the parts of MO2's Qt-style ini the importer uses.</summary>
public static class Mo2Ini
{
    private const string ByteArrayPrefix = "@ByteArray(";

    public static Mo2IniDocument Parse(string text)
    {
        var document = new Mo2IniDocument();
        var section = "General";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            document.Set(section, line[..equals].Trim(), Unescape(line[(equals + 1)..]));
        }
        return document;
    }

    public static string? SelectedProfile(Mo2IniDocument document) => document.Get("General", "selected_profile");

    /// <summary>The <c>[customExecutables]</c> entries in the order MO2 numbers them.</summary>
    public static IReadOnlyList<Mo2Executable> CustomExecutables(Mo2IniDocument document)
    {
        var fields = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (var (key, value) in document.Section("customExecutables"))
        {
            var slash = key.IndexOf('\\');
            if (slash <= 0 || !int.TryParse(key.AsSpan(0, slash), out var index)) continue;

            if (!fields.TryGetValue(index, out var entry))
                fields[index] = entry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            entry[key[(slash + 1)..]] = value;
        }

        return fields.Values
            .Select(entry => new Mo2Executable(
                entry.GetValueOrDefault("title", ""),
                entry.GetValueOrDefault("binary", ""),
                entry.GetValueOrDefault("arguments", "")))
            .Where(executable => executable.Binary.Length > 0)
            .ToList();
    }

    /// <summary>Qt wraps a value holding a comma in quotes; escaped quotes inside it are left for <see cref="Unescape"/>.</summary>
    private static string StripWrappingQuotes(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;

        // A closing quote preceded by an odd number of backslashes is itself escaped, so the value is not wrapped.
        var backslashes = 0;
        for (var i = value.Length - 2; i >= 1 && value[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 0 ? value[1..^1] : value;
    }

    private static string Unescape(string value)
    {
        value = value.TrimEnd('\r');
        if (value.StartsWith(ByteArrayPrefix, StringComparison.Ordinal) && value.EndsWith(')'))
            value = value[ByteArrayPrefix.Length..^1];

        value = StripWrappingQuotes(value);
        if (!value.Contains('\\')) return value;

        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '\\' or '"')
            {
                result.Append(value[i + 1]);
                i++;
            }
            else
            {
                result.Append(value[i]);
            }
        }
        return result.ToString();
    }
}
