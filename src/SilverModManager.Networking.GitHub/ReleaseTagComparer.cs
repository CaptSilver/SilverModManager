using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using SilverModManager.Networking.GitHub.DTOs;

namespace SilverModManager.Networking.GitHub;

/// <summary>
/// Comparer for <see cref="Release"/> based on semantic versions in <see cref="Release.TagName"/>.
/// </summary>
[PublicAPI]
public class ReleaseTagComparer : IComparer<Release>
{
    /// <summary>
    /// Instance.
    /// </summary>
    public static readonly IComparer<Release> Instance = new ReleaseTagComparer();

    /// <inheritdoc/>
    public int Compare(Release? x, Release? y)
    {
        if (x is null && y is null) return 0;
        if (x is null && y is not null) return -1;
        if (x is not null && y is null) return 1;

        Debug.Assert(x is not null);
        Debug.Assert(y is not null);

        if (!x.TryGetVersion(out var a)) return -1;
        if (!y.TryGetVersion(out var b)) return -1;

        return a.CompareTo(b);
    }
}

/// <summary>
/// Extension methods.
/// </summary>
[PublicAPI]
public static class ReleaseExtensions
{
    /// <summary>
    /// Tries to parse the tag name as a semantic version.
    /// </summary>
    public static bool TryGetVersion(this Release release, [NotNullWhen(true)] out Version? version)
        => TryParseTag(release.TagName, out version);

    /// <summary>
    /// Tries to parse a release tag such as <c>v2.2.6</c> or <c>2.2.6</c> as a version. A tag that is only a number
    /// (<c>v12</c>) is that major version. A tag that is a date or a name is not a version.
    /// </summary>
    public static bool TryParseTag(string tag, [NotNullWhen(true)] out Version? version)
    {
        var tagName = tag.AsSpan();
        if (tagName.StartsWith('v'))
            tagName = tagName[1..];

        if (int.TryParse(tagName, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major))
        {
            version = new Version(major, 0);
            return true;
        }

        return Version.TryParse(tagName, out version);
    }
}
