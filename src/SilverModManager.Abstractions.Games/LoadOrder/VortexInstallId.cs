using System.Text.RegularExpressions;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>What a Vortex install id says about the download it came from.</summary>
/// <param name="Name">The mod name as Vortex wrote it.</param>
/// <param name="NexusModId">The Nexus mod page.</param>
/// <param name="Version">The version, or null for the archive form, which turns dots into dashes and so can't be told from a version that has dashes.</param>
public readonly partial record struct VortexInstallId(string Name, uint NexusModId, string? Version)
{
    // Collection installs: "<name> <modId> <version> <yyyy-MM-ddTHH-mm[-ss]Z> <shortid>". Anchored on the tail because names hold spaces and digits.
    [GeneratedRegex(@"^(?<name>.+?) (?<modId>\d+) (?<version>\S+) \d{4}-\d{2}-\d{2}T\d{2}-\d{2}(?:-\d{2})?Z \S+$", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedPattern();

    // Archive names: "<name>-<modId>-<version, dashes for dots>-<unix time>". Anchored on the trailing unix time so a number in the
    // name is not read as the mod id. A version that starts with a digit (or v+digit) is preferred so "Witcher-3-HD-1234-..." skips
    // the 3; only when no such split exists does DashedAnyVersionPattern take a letter-first version ("Mod-123-beta-...").
    [GeneratedRegex(@"^(?<name>.+?)-(?<modId>\d+)-(?:v?\d[0-9A-Za-z-]*?-)??(?<time>\d{9,})$", RegexOptions.CultureInvariant)]
    private static partial Regex DashedPattern();

    // The fallback only sees ids the primary pattern rejected, so its version starts with a letter. The name is greedy: the mod id is the
    // last number that is followed by a letter-first version, so "Witcher-3-HD-1234-beta-..." is mod 1234 and not mod 3.
    [GeneratedRegex(@"^(?<name>.+)-(?<modId>\d+)-(?:[A-Za-z][0-9A-Za-z-]*?-)??(?<time>\d{9,})$", RegexOptions.CultureInvariant)]
    private static partial Regex DashedAnyVersionPattern();

    /// <summary>Parses either form. Anything else is not an install id; there is no fallback to "the first number between dashes".</summary>
    public static bool TryParse(string id, out VortexInstallId parsed)
    {
        if (SpacedPattern().Match(id) is { Success: true } spaced && uint.TryParse(spaced.Groups["modId"].Value, out var spacedId))
        {
            parsed = new VortexInstallId(spaced.Groups["name"].Value, spacedId, spaced.Groups["version"].Value);
            return true;
        }

        var dashed = DashedPattern().Match(id);
        if (!dashed.Success) dashed = DashedAnyVersionPattern().Match(id);
        if (dashed.Success && uint.TryParse(dashed.Groups["modId"].Value, out var dashedId))
        {
            parsed = new VortexInstallId(dashed.Groups["name"].Value, dashedId, null);
            return true;
        }

        parsed = default;
        return false;
    }

    /// <summary>
    /// Every way an archive-form id can be split into name, mod id and version, the split <see cref="TryParse"/> prefers first.
    /// Any numeric segment could be the mod id, so a caller that has the collection's mod list picks the one that exists there.
    /// Empty for the spaced form and for anything that is not an install id.
    /// </summary>
    /// <returns>The splits; the version is the dashed rest of the id, empty when the id has none.</returns>
    public static IReadOnlyList<(string Name, uint NexusModId, string DashedVersion)> DashedCandidates(string id)
    {
        var tail = DashedTailPattern().Match(id);
        if (!tail.Success) return [];

        var segments = tail.Groups["rest"].Value.Split('-');
        var all = new List<(string Name, uint NexusModId, string DashedVersion)>();
        for (var i = 1; i < segments.Length; i++)
        {
            if (!uint.TryParse(segments[i], out var modId) || segments[i].Any(c => !char.IsAsciiDigit(c))) continue;
            all.Add((string.Join('-', segments[..i]), modId, string.Join('-', segments[(i + 1)..])));
        }

        if (TryParse(id, out var preferred) && preferred.Version is null)
        {
            var index = all.FindIndex(c => c.Name == preferred.Name && c.NexusModId == preferred.NexusModId);
            if (index > 0) (all[0], all[index]) = (all[index], all[0]);
        }

        return all;
    }

    [GeneratedRegex(@"^(?<rest>.+)-\d{9,}$", RegexOptions.CultureInvariant)]
    private static partial Regex DashedTailPattern();
}
