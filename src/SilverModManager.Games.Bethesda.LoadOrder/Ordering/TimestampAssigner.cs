// Ported from libloadorder src/load_order/timestamp_based.rs (padded_unique_timestamps).
namespace NexusMods.Games.Bethesda.LoadOrder.Ordering;

/// <summary>
/// Picks the modification times a timestamp-ordered game reads its load order from. The plugins'
/// own times are reused rather than inventing a base date, so the files keep roughly the dates they had.
/// </summary>
public static class TimestampAssigner
{
    private static readonly TimeSpan Pad = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Sorts and de-duplicates <paramref name="existing"/>, then adds timestamps 60 seconds apart after the last
    /// until there are <paramref name="count"/>. Returned ascending; the caller zips them with the resolved order.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Assign(IReadOnlyList<DateTimeOffset> existing, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var result = existing.Distinct().Order().ToList();
        while (result.Count < count)
            result.Add((result.Count > 0 ? result[^1] : DateTimeOffset.UnixEpoch) + Pad);

        // More distinct times than plugins: keep the earliest, one per plugin.
        if (result.Count > count) result.RemoveRange(count, result.Count - count);
        return result;
    }
}
