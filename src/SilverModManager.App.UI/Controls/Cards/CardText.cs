using System.Globalization;

namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// The text a store card shows in place of raw numbers.
/// </summary>
public static class CardText
{
    /// <summary>1,234,567 as "1.2M"; one decimal below ten of a unit, none above.</summary>
    public static string CompactCount(long count)
    {
        if (count <= 0) return "0";
        if (count < 1_000) return count.ToString(CultureInfo.InvariantCulture);

        var (divisor, suffix) = count switch
        {
            < 1_000_000 => (1_000d, "K"),
            < 1_000_000_000 => (1_000_000d, "M"),
            _ => (1_000_000_000d, "B"),
        };

        // Truncate rather than round, so 999,999 reads 999K and never "1000K".
        var scaled = Math.Floor(count / divisor * 10) / 10;
        return (scaled >= 10 ? Math.Floor(scaled).ToString(CultureInfo.InvariantCulture) : scaled.ToString("0.#", CultureInfo.InvariantCulture)) + suffix;
    }
}
