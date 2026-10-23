namespace SilverModManager.Wabbajack.Mapping;

/// <summary>One line of a profile's modlist.txt.</summary>
/// <param name="Name">The mod folder name, or the separator's name including its <c>_separator</c> suffix.</param>
/// <param name="IsForeign">A mod MO2 knows without a folder in <c>mods</c> (the game's DLC), written with <c>*</c>.</param>
public readonly record struct ModlistTxtEntry(string Name, bool Enabled, bool IsSeparator, bool IsForeign);

/// <summary>Reads MO2's modlist.txt.</summary>
public static class ModlistTxt
{
    private const string SeparatorSuffix = "_separator";

    /// <summary>
    /// The entries in file order. MO2 writes the highest priority first (the file is a top-down view of the mod
    /// list), so the first entry wins file conflicts.
    /// </summary>
    public static IReadOnlyList<ModlistTxtEntry> Parse(string text)
    {
        var entries = new List<ModlistTxtEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[0] == '#') continue;

            var prefix = line[0];
            if (prefix is not ('+' or '-' or '*')) continue;

            var name = line[1..];
            entries.Add(new ModlistTxtEntry(
                name,
                Enabled: prefix != '-',
                IsSeparator: name.EndsWith(SeparatorSuffix, StringComparison.OrdinalIgnoreCase),
                IsForeign: prefix == '*'));
        }
        return entries;
    }
}
