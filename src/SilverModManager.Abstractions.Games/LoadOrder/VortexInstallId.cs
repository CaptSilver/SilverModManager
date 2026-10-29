using System.Text.RegularExpressions;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>What a Vortex install id says about the download it came from.</summary>
/// <param name="Name">The mod name as Vortex wrote it.</param>
/// <param name="NexusModId">The Nexus mod page.</param>
/// <param name="Version">The version, or null for the archive form, which turns dots into dashes and so can't be told from a version that has dashes.</param>
public readonly partial record struct VortexInstallId(string Name, uint NexusModId, string? Version)
{
    // Collection installs: "<name> <modId> <version> <yyyy-MM-ddTHH-mmZ> <shortid>". Anchored on the tail because names hold spaces and digits.
    [GeneratedRegex(@"^(?<name>.+?) (?<modId>\d+) (?<version>\S+) \d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z \S+$", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedPattern();

    // Archive names: "<name>-<modId>-<version, dashes for dots>-<unix time>". Anchored on the trailing unix time so a number in the
    // name is not read as the mod id; the version starts with a digit (or v+digit) so "Witcher-3-HD-1234-..." skips the 3.
    [GeneratedRegex(@"^(?<name>.+?)-(?<modId>\d+)-(?:v?\d[0-9A-Za-z-]*?-)??(?<time>\d{9,})$", RegexOptions.CultureInvariant)]
    private static partial Regex DashedPattern();

    /// <summary>Parses either form. Anything else is not an install id; there is no fallback to "the first number between dashes".</summary>
    public static bool TryParse(string id, out VortexInstallId parsed)
    {
        if (SpacedPattern().Match(id) is { Success: true } spaced && uint.TryParse(spaced.Groups["modId"].Value, out var spacedId))
        {
            parsed = new VortexInstallId(spaced.Groups["name"].Value, spacedId, spaced.Groups["version"].Value);
            return true;
        }

        if (DashedPattern().Match(id) is { Success: true } dashed && uint.TryParse(dashed.Groups["modId"].Value, out var dashedId))
        {
            parsed = new VortexInstallId(dashed.Groups["name"].Value, dashedId, null);
            return true;
        }

        parsed = default;
        return false;
    }
}
