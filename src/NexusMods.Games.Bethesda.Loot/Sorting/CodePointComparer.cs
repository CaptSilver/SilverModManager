using System.Text;

namespace NexusMods.Games.Bethesda.Loot.Sorting;

/// <summary>
/// Orders strings by Unicode code point, which matches Rust's byte-wise comparison of UTF-8 strings.
/// </summary>
internal sealed class CodePointComparer : IComparer<string>
{
    public static readonly CodePointComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var xs = x.EnumerateRunes();
        var ys = y.EnumerateRunes();
        using var xe = xs.GetEnumerator();
        using var ye = ys.GetEnumerator();
        while (true)
        {
            var hasX = xe.MoveNext();
            var hasY = ye.MoveNext();
            if (!hasX || !hasY) return hasX.CompareTo(hasY);
            var cmp = xe.Current.Value.CompareTo(ye.Current.Value);
            if (cmp != 0) return cmp;
        }
    }
}
