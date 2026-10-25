using System.Globalization;
using System.Text.RegularExpressions;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <summary>
/// What the name of an archive downloaded from Nexus Mods says about it. Nexus Mods has named its files in two shapes:
/// the older <c>Name-&lt;modId&gt;-&lt;version with dots as dashes&gt;-&lt;unix seconds&gt;.ext</c> and the newer
/// <c>Name &lt;modId&gt; &lt;version&gt; &lt;yyyy-MM-ddTHH-mmZ&gt; &lt;random&gt;.ext</c>.
/// </summary>
/// <param name="Name">The mod's name as it stands in the archive name.</param>
/// <param name="ModId">The Nexus Mods id of the mod page.</param>
/// <param name="Version">The version as the name spells it.</param>
/// <param name="Uploaded">When the file was uploaded, to the second or only to the minute.</param>
/// <param name="MinuteResolution">True when <paramref name="Uploaded"/> was written without seconds.</param>
public sealed partial record ArchiveName(string Name, uint ModId, string Version, DateTimeOffset Uploaded, bool MinuteResolution)
{
    // Unix seconds between 2001 and 2100; anything else at the end of a name is not an upload time.
    private const long EarliestUnix = 978_307_200;
    private const long LatestUnix = 4_102_444_800;

    [GeneratedRegex(@"^(?<name>.+) (?<mod>\d{1,10}) (?<ver>\S+) (?<ts>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z) (?<rand>[A-Za-z0-9_]+)$")]
    private static partial Regex NewStyle();

    [GeneratedRegex(@"^(?<body>.+)-(?<ts>\d{9,11})$")]
    private static partial Regex OldStyleTail();

    [GeneratedRegex(@"\.(?:tar\.)?[A-Za-z0-9]{1,5}$")]
    private static partial Regex Extension();

    /// <summary>
    /// Reads <paramref name="asset"/>, a file name from a lock. <paramref name="version"/> is the version the lock
    /// gives for the same mod; the old shape needs it to tell the mod id from the dashes of the version. Returns null
    /// when the name is neither shape or has more than one reading.
    /// </summary>
    public static ArchiveName? TryParse(string asset, string? version)
    {
        if (string.IsNullOrWhiteSpace(asset)) return null;

        var stem = Extension().Replace(asset, "");
        return TryNewStyle(stem) ?? TryOldStyle(stem, version);
    }

    private static ArchiveName? TryNewStyle(string stem)
    {
        var match = NewStyle().Match(stem);
        if (!match.Success || !uint.TryParse(match.Groups["mod"].Value, out var modId) || modId == 0) return null;
        if (!DateTimeOffset.TryParseExact(match.Groups["ts"].Value, "yyyy-MM-dd'T'HH-mm'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var uploaded))
            return null;

        return new ArchiveName(match.Groups["name"].Value, modId, match.Groups["ver"].Value, uploaded, MinuteResolution: true);
    }

    private static ArchiveName? TryOldStyle(string stem, string? version)
    {
        var tail = OldStyleTail().Match(stem);
        if (!tail.Success || !long.TryParse(tail.Groups["ts"].Value, out var unix) || unix is < EarliestUnix or > LatestUnix) return null;

        var body = tail.Groups["body"].Value;
        var wanted = version is null ? null : Dashed(version);
        ArchiveName? found = null;
        for (var dash = body.IndexOf('-'); dash >= 0; dash = body.IndexOf('-', dash + 1))
        {
            var digits = 0;
            while (dash + 1 + digits < body.Length && char.IsAsciiDigit(body[dash + 1 + digits])) digits++;

            var next = dash + 1 + digits;
            if (digits is 0 or > 10 || next >= body.Length - 1 || body[next] != '-') continue;
            if (!uint.TryParse(body.AsSpan(dash + 1, digits), out var modId) || modId == 0) continue;

            var spelled = body[(next + 1)..];
            if (wanted is not null && Dashed(spelled) != wanted) continue;

            // Without a version to check against, a second reading means the name cannot be trusted.
            if (found is not null) return null;
            found = new ArchiveName(body[..dash], modId, spelled, DateTimeOffset.FromUnixTimeSeconds(unix), MinuteResolution: false);
        }

        return found;
    }

    /// <summary>A version with the separators and a leading <c>v</c> taken out, so "v1.1.3" and "1-1-3" compare equal.</summary>
    internal static string Dashed(string version)
    {
        var lowered = version.Trim().ToLowerInvariant();
        if (lowered.Length > 1 && lowered[0] == 'v' && char.IsAsciiDigit(lowered[1])) lowered = lowered[1..];
        return string.Join('-', lowered.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries));
    }
}
