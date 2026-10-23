using System.Text.RegularExpressions;

namespace NexusMods.Networking.ModUpdates;

/// <summary>
/// Tells which variant of a mod a Nexus file is, independent of its version, so that a mod page with
/// several parallel downloads (a lite edition, a regional patch, a different base game version) does not
/// have its siblings mistaken for updates of each other.
/// </summary>
/// <remarks>
/// Nexus file names come in two shapes, and both bury the variant name in front of machine noise:
/// <c>Name-&lt;modid&gt;-&lt;dashed version&gt;-&lt;epoch&gt;.ext</c> (legacy) and
/// <c>Name &lt;modid&gt; &lt;version&gt; &lt;ISO stamp&gt; &lt;token&gt;.ext</c> (current).
/// </remarks>
public static partial class VariantKey
{
    [GeneratedRegex(@"\.(zip|7z|rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveExtension();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z")]
    private static partial Regex IsoStamp();

    [GeneratedRegex(@"\b\d{9,}\b")]
    private static partial Regex Epoch();

    [GeneratedRegex(@"\s+[A-Za-z0-9]{9}\s*$")]
    private static partial Regex TrailingToken();

    [GeneratedRegex(@"[\s\-]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"[.\-_]")]
    private static partial Regex VersionParts();

    /// <summary>
    /// The variant of <paramref name="fileName"/>: lowercase words with the extension, timestamp, epoch,
    /// trailing token and every token of the version removed.
    /// </summary>
    /// <param name="fileName">The file name as listed on the mod page.</param>
    /// <param name="version">The version Nexus lists for the file, if any.</param>
    /// <param name="modId">
    /// The mod page's id, if known. Files of one page all carry it, so it only matters when a key is shown
    /// or compared across pages.
    /// </param>
    /// <remarks>
    /// The text is normalised before it is split into words. Splitting first shatters an ISO stamp
    /// (<c>2026-07-14T10-47Z</c>) into <c>2026</c>, <c>07</c>, <c>14t10</c> and <c>47z</c>, which then read as
    /// name words. Version tokens are removed whole, never as substrings: a mod whose version is "1" would
    /// otherwise lose every "1" in its name.
    /// </remarks>
    public static string Of(string fileName, string? version, ulong? modId = null)
    {
        var stem = ArchiveExtension().Replace(fileName, "");
        stem = IsoStamp().Replace(stem, "");
        stem = Epoch().Replace(stem, "");
        stem = TrailingToken().Replace(stem, "");

        var drop = new HashSet<string>(StringComparer.Ordinal);
        if (modId is { } id) drop.Add(id.ToString());
        if (!string.IsNullOrWhiteSpace(version))
        {
            var lower = version.ToLowerInvariant();
            var bare = lower.TrimStart('v');
            drop.Add(lower);
            drop.Add(bare);
            foreach (var part in VersionParts().Split(bare))
            {
                if (part.Length > 0) drop.Add(part);
            }
        }

        var words = Separators().Split(stem)
            .Where(word => word.Length > 0)
            .Select(word => word.ToLowerInvariant())
            .Where(word => !drop.Contains(word) && !drop.Contains(word.TrimStart('v')));

        return string.Join(' ', words).Trim();
    }
}
