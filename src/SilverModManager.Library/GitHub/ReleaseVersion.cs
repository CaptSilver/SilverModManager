using System.Diagnostics.CodeAnalysis;
using SilverModManager.Networking.GitHub;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// A release tag read as a version, with the prerelease suffix kept apart: <c>v3.0.0-rc1</c> is the version 3.0.0 as a
/// prerelease, which is older than the 3.0.0 that follows it and newer than any 2.x.
/// </summary>
internal readonly record struct ReleaseVersion(Version Core, bool IsPrerelease) : IComparable<ReleaseVersion>
{
    /// <summary>False for a tag that is a date or a name, which orders against nothing.</summary>
    public static bool TryParse(string tag, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        var end = tag.IndexOfAny(['-', '+']);
        var core = end < 0 ? tag : tag[..end];

        // A prerelease label is a word (rc1, beta.2). A suffix that starts with a digit makes the tag a date such as
        // 2026-11-01, which says nothing about order.
        var suffixIsLabel = end < 0 || (end + 1 < tag.Length && char.IsAsciiLetter(tag[end + 1]));
        if (!suffixIsLabel || !ReleaseExtensions.TryParseTag(core, out var parsed))
        {
            version = null;
            return false;
        }

        // A suffix after a plus is build metadata, which does not make a release a prerelease.
        version = new ReleaseVersion(parsed, end >= 0 && tag[end] == '-');
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var byCore = Core.CompareTo(other.Core);
        return byCore != 0 ? byCore : other.IsPrerelease.CompareTo(IsPrerelease);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
}
