using System.Text.RegularExpressions;
using SilverModManager.Games.Bethesda.Loot.Database;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

// Follows libloot 0.29.6's src/plugin/mod.rs (extract_version).

/// <summary>
/// Pulls a version number out of a plugin header's description text, the way LOOT does for
/// <c>version()</c> conditions on plugins.
/// </summary>
public static class PluginDescriptionVersion
{
    // Pseudosem v1.0.1 without space separators, which make extraction from sentences unreliable. The final group
    // stops a number followed by a comma ("2,300") from matching.
    private const string Pseudosem = @"(\d+(?:\.\d+)+(?:[-._:]?[A-Za-z0-9]+)*)(?:[^,]|\z)";

    // Tried in priority order; the first that matches wins.
    private static readonly Regex[] Patterns =
    [
        // Timestamps with slashes, as a Bashed Patch writes them.
        Compile(@"(\d{1,2}/\d{1,2}/\d{1,4} \d{1,2}:\d{1,2}:\d{1,2})"),
        Compile($@"version:?\s{Pseudosem}"),
        Compile($@"(?:^|v|\s){Pseudosem}"),

        // A bare number at the start, or after 'v' or 'version:'.
        Compile(@"(?:^|v|version:\s*)(\d+)"),
    ];

    /// <summary>The version in <paramref name="description"/>, or null when there is none or the description is null.</summary>
    public static string? Extract(string? description)
    {
        if (description is null) return null;

        foreach (var pattern in Patterns)
        {
            var match = pattern.Match(description);
            if (match.Success && match.Groups[1].Success) return match.Groups[1].Value.Trim();
        }

        return null;
    }

    private static Regex Compile(string pattern) => new(pattern, ConditionRegex.Options);
}
