using System.Globalization;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Thunderstore;

/// <summary>
/// A Thunderstore package version: <c>Major.Minor.Patch</c>, each part a whole number. Compared numerically per
/// part, so 1.0.10 is newer than 1.0.9, which string order gets wrong.
/// </summary>
[PublicAPI]
public readonly record struct ThunderstoreVersionNumber(uint Major, uint Minor, uint Patch) : IComparable<ThunderstoreVersionNumber>
{
    /// <summary>
    /// Parses exactly three dot-separated parts made of digits.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out ThunderstoreVersionNumber version)
    {
        version = default;

        var firstDot = text.IndexOf('.');
        if (firstDot < 0) return false;
        var rest = text[(firstDot + 1)..];
        var secondDot = rest.IndexOf('.');
        if (secondDot < 0) return false;

        if (!TryParsePart(text[..firstDot], out var major)) return false;
        if (!TryParsePart(rest[..secondDot], out var minor)) return false;
        if (!TryParsePart(rest[(secondDot + 1)..], out var patch)) return false;

        version = new ThunderstoreVersionNumber(major, minor, patch);
        return true;
    }

    // NumberStyles.None rejects signs, spaces and a nested dot, which the part must not contain.
    private static bool TryParsePart(ReadOnlySpan<char> part, out uint value)
        => uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <inheritdoc/>
    public int CompareTo(ThunderstoreVersionNumber other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ThunderstoreVersionNumber left, ThunderstoreVersionNumber right) => left.CompareTo(right) < 0;
    public static bool operator >(ThunderstoreVersionNumber left, ThunderstoreVersionNumber right) => left.CompareTo(right) > 0;
    public static bool operator <=(ThunderstoreVersionNumber left, ThunderstoreVersionNumber right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ThunderstoreVersionNumber left, ThunderstoreVersionNumber right) => left.CompareTo(right) >= 0;

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
