namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>
/// The sections and keys of a Qt-style ini such as MO2's, read leniently: a line that makes no sense is skipped, never
/// an error. Section and key names compare ignoring case. Values lose surrounding quotes and an <c>@ByteArray(...)</c> wrapper.
/// </summary>
internal sealed class IniText
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static IniText Parse(string text)
    {
        var ini = new IniText();
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

            if (!ini._sections.TryGetValue(section, out var keys))
                ini._sections[section] = keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            keys[line[..equals].Trim()] = Unwrap(line[(equals + 1)..].Trim());
        }

        return ini;
    }

    public string? Get(string section, string key)
        => _sections.TryGetValue(section, out var keys) && keys.TryGetValue(key, out var value) ? value : null;

    public IReadOnlyDictionary<string, string> Section(string section)
        => _sections.TryGetValue(section, out var keys) ? keys : new Dictionary<string, string>();

    private static string Unwrap(string value)
    {
        const string prefix = "@ByteArray(";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith(')')) value = value[prefix.Length..^1];
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return value;
    }
}
