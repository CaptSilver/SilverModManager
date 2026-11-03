using System.Text.RegularExpressions;
using SilverModManager.Games.Bethesda.Loot.Database;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

// Follows loot-condition-interpreter's function/version.rs (Version, ReleaseId, PreReleaseId).

/// <summary>
/// A version as the <c>version()</c> family of conditions compares them: release ids split on <c>.</c> and
/// <c>,</c>, then optional pre-release ids after the first of <c>- :_</c>, with <c>+metadata</c> ignored. It is
/// more forgiving than semver: ids need not be numeric, and a shorter release is padded with zeroes.
/// </summary>
public sealed partial class LootVersion : IComparable<LootVersion>, IEquatable<LootVersion>
{
    private readonly ReleaseId[] _release;
    private readonly PreReleaseId[] _preRelease;

    private LootVersion(ReleaseId[] release, PreReleaseId[] preRelease)
    {
        _release = release;
        _preRelease = preRelease;
    }

    public static LootVersion Parse(string text)
    {
        var (release, preRelease) = Split(TrimMetadata(text));

        return new LootVersion(
            release.Split('.', ',').Select(ReleaseId.From).ToArray(),
            SplitTerminator(preRelease).Select(PreReleaseId.From).ToArray());
    }

    /// <summary>Whether <c>this &lt;op&gt; <paramref name="other"/></c> holds.</summary>
    public bool Compare(ComparisonOperator op, LootVersion other) => op switch
    {
        ComparisonOperator.Equal => Equals(other),
        ComparisonOperator.NotEqual => !Equals(other),
        ComparisonOperator.LessThan => CompareTo(other) < 0,
        ComparisonOperator.GreaterThan => CompareTo(other) > 0,
        ComparisonOperator.LessThanOrEqual => CompareTo(other) <= 0,
        ComparisonOperator.GreaterThanOrEqual => CompareTo(other) >= 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    public int CompareTo(LootVersion? other)
    {
        if (other is null) return 1;

        var (left, right) = Pad(_release, other._release);
        for (var i = 0; i < left.Length; i++)
        {
            var order = left[i].CompareTo(right[i]);
            if (order != 0) return order;
        }

        return (_preRelease.Length == 0, other._preRelease.Length == 0) switch
        {
            (true, false) => 1,
            (false, true) => -1,
            _ => ComparePreRelease(_preRelease, other._preRelease),
        };
    }

    public bool Equals(LootVersion? other)
    {
        if (other is null) return false;

        var (left, right) = Pad(_release, other._release);
        return left.AsSpan().SequenceEqual(right) && _preRelease.AsSpan().SequenceEqual(other._preRelease);
    }

    public override bool Equals(object? obj) => obj is LootVersion other && Equals(other);

    // Equal versions can differ in padding and in how a number is written, so only the pre-release count is safe to hash.
    public override int GetHashCode() => _preRelease.Length;

    private static int ComparePreRelease(PreReleaseId[] left, PreReleaseId[] right)
    {
        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            var order = left[i].CompareTo(right[i]);
            if (order != 0) return order;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static (ReleaseId[] Left, ReleaseId[] Right) Pad(ReleaseId[] left, ReleaseId[] right)
    {
        if (left.Length == right.Length) return (left, right);

        var length = Math.Max(left.Length, right.Length);
        return (PadTo(left, length), PadTo(right, length));
    }

    private static ReleaseId[] PadTo(ReleaseId[] ids, int length)
    {
        if (ids.Length == length) return ids;

        var padded = new ReleaseId[length];
        ids.CopyTo(padded, 0);
        Array.Fill(padded, ReleaseId.Zero, ids.Length, length - ids.Length);
        return padded;
    }

    private static string TrimMetadata(string version)
    {
        if (version.Length == 0) return "0";

        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private static (string Release, string PreRelease) Split(string text)
    {
        // OBSE and SKSE write "0, 1, 2, 3", whose spaces would otherwise start the pre-release part.
        if (PlatformVersion().IsMatch(text)) return (text, "");

        var at = text.AsSpan().IndexOfAny("- :_");
        return at < 0 ? (text, "") : (text[..at], text[(at + 1)..]);
    }

    /// <summary>Rust's <c>split_terminator</c>: like split, but a final empty piece is dropped.</summary>
    private static IEnumerable<string> SplitTerminator(string text)
    {
        if (text.Length == 0) return [];

        var pieces = text.Split('.', '-', ' ', ':', '_');
        return pieces[^1].Length == 0 ? pieces[..^1] : pieces;
    }

    [GeneratedRegex(@"\d+, \d+, \d+, \d+")]
    private static partial Regex PlatformVersion();

    /// <summary>Rust's <c>str::parse::&lt;u32&gt;</c> after a trim: an optional plus sign, then ASCII digits that fit.</summary>
    internal static bool TryParseNumber(string text, out uint value)
    {
        value = 0;
        var digits = text.Trim().AsSpan();
        if (digits.Length > 1 && digits[0] == '+') digits = digits[1..];
        if (digits.Length == 0) return false;

        ulong total = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return false;
            total = total * 10 + (ulong)(c - '0');
            if (total > uint.MaxValue) return false;
        }

        value = (uint)total;
        return true;
    }

    private readonly record struct ReleaseId(bool IsNumeric, uint Number, string Text) : IEquatable<ReleaseId>
    {
        public static ReleaseId Zero { get; } = new(true, 0, "");

        public static ReleaseId From(string text) =>
            TryParseNumber(text, out var number) ? new ReleaseId(true, number, "") : new ReleaseId(false, 0, text.ToLowerInvariant());

        public bool Equals(ReleaseId other)
        {
            if (IsNumeric && other.IsNumeric) return Number == other.Number;
            if (!IsNumeric && !other.IsNumeric) return Text == other.Text;

            var (number, text) = IsNumeric ? (Number, other.Text) : (other.Number, Text);
            return TryParseNumber(text, out var parsed) && parsed == number;
        }

        public override int GetHashCode() => 0;

        public int CompareTo(ReleaseId other)
        {
            if (IsNumeric && other.IsNumeric) return Number.CompareTo(other.Number);
            if (!IsNumeric && !other.IsNumeric) return string.CompareOrdinal(Text, other.Text);

            return IsNumeric ? CompareNumberToText(Number, other.Text) : -CompareNumberToText(other.Number, Text);
        }

        // A string is compared by its leading digits. When they match and more follows, the number is the lesser.
        private static int CompareNumberToText(uint number, string text)
        {
            var digitsEnd = 0;
            while (digitsEnd < text.Length && text[digitsEnd] is >= '0' and <= '9') digitsEnd++;

            var hasMore = digitsEnd < text.Length;
            if (digitsEnd == 0 || !uint.TryParse(text.AsSpan(0, digitsEnd), out var leading)) return -1;

            var order = number.CompareTo(leading);
            return order == 0 && hasMore ? -1 : order;
        }
    }

    private readonly record struct PreReleaseId(bool IsNumeric, uint Number, string Text) : IEquatable<PreReleaseId>
    {
        public static PreReleaseId From(string text) =>
            TryParseNumber(text, out var number) ? new PreReleaseId(true, number, "") : new PreReleaseId(false, 0, text.ToLowerInvariant());

        // Numeric ids sort before text ones.
        public int CompareTo(PreReleaseId other)
        {
            if (IsNumeric != other.IsNumeric) return IsNumeric ? -1 : 1;
            return IsNumeric ? Number.CompareTo(other.Number) : string.CompareOrdinal(Text, other.Text);
        }
    }
}
