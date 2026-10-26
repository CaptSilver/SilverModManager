// Follows libloadorder src/ini.rs (the [Game Files] section of Morrowind.ini).
using System.Text.RegularExpressions;
using SilverModManager.Sdk.Ini;

namespace SilverModManager.Games.Bethesda.LoadOrder.Formats;

/// <summary>
/// The <c>[Game Files]</c> section of Morrowind.ini: <c>GameFile0=Morrowind.esm</c>, <c>GameFile1=...</c>, one
/// line per active plugin. Everything outside that section's <c>GameFileN</c> lines is left as it is.
/// </summary>
public static partial class MorrowindIniGameFiles
{
    private const string SectionName = "Game Files";

    [GeneratedRegex(@"^GameFile(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameFileKey();

    /// <summary>The plugin names of the section, in the order of their numbers. Blank values are skipped.</summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        var found = new List<(long Number, int Position, string Name)>();
        var inSection = false;
        foreach (var line in IniDocument.Parse(text).Lines)
        {
            if (line.Kind == IniLineKind.Section)
            {
                // Rewrite writes into the first [Game Files] block, so once a block has entries a repeat further down is stale.
                inSection = line.Name.Equals(SectionName, StringComparison.OrdinalIgnoreCase);
                if (!inSection && found.Count > 0) break;
                continue;
            }

            if (!inSection || line.Kind != IniLineKind.KeyValue) continue;
            if (GameFileKey().Match(line.Name) is not { Success: true } match || line.Value.Length == 0) continue;

            // A number too large for a long cannot be a real slot; order it last instead of throwing.
            found.Add((long.TryParse(match.Groups[1].Value, out var number) ? number : long.MaxValue, found.Count, line.Value));
        }

        return found.OrderBy(f => f.Number).ThenBy(f => f.Position).Select(f => f.Name).ToList();
    }

    /// <summary>
    /// Replaces the <c>GameFileN</c> lines of the section with one line per name, numbered from 0. The new lines
    /// take the place of the first old one; with none, they follow the section's last line. A file without the
    /// section gets it at the end.
    /// </summary>
    public static string Rewrite(string existing, IReadOnlyList<string> names)
    {
        var document = IniDocument.Parse(existing);
        document.ReplaceKeys(SectionName, key => GameFileKey().IsMatch(key),
            names.Select((name, i) => KeyValuePair.Create($"GameFile{i}", name)).ToList());
        return document.ToText();
    }
}
