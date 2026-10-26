namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>The check every three-way merge over binders makes before it looks at a single member.</summary>
public static class BinderEntrySets
{
    private const int Shown = 5;

    /// <summary>
    /// Refuses three archives that do not hold the same members. A rebuild takes its member list from the base, so a
    /// member one side lacks has nowhere to go, and "only the other side moved" cannot be told from "the base authored
    /// this and vanilla never carried it"; guessing wrong silently discards the preferred mod's own content. Names the
    /// ids that differ and who holds them, capped so a regulation with hundreds of members doesn't flood a report.
    /// </summary>
    /// <param name="noun">What the three are, plural: "regulations", "archives".</param>
    /// <param name="refuse">Builds the exception to throw from the message; a <see cref="MergeException"/> when omitted.</param>
    /// <param name="baseName">The mod that owns the base, for the message; "base" when null.</param>
    /// <param name="otherName">The mod that owns the other side, for the message; "other" when null.</param>
    public static void RequireSameEntries(
        IEnumerable<int> @base, IEnumerable<int> other, IEnumerable<int> vanilla, string noun,
        Func<string, Exception>? refuse = null, string? baseName = null, string? otherName = null)
    {
        var sides = new (string Name, HashSet<int> Ids)[] { (baseName ?? "base", [.. @base]), (otherName ?? "other", [.. other]), ("vanilla", [.. vanilla]) };
        var all = sides.SelectMany(s => s.Ids).ToHashSet();
        var odd = all.Where(id => !sides.All(s => s.Ids.Contains(id))).Order().ToArray();
        if (odd.Length == 0) return;

        var listed = string.Join("; ", odd.Take(Shown).Select(id =>
            $"entry {id}: in {string.Join(", ", sides.Where(s => s.Ids.Contains(id)).Select(s => s.Name))}"));
        var more = odd.Length > Shown ? $" (and {odd.Length - Shown} more)" : "";
        var message = $"the three {noun} hold different entries ({listed}{more}); merging would silently drop whichever side is missing from the structural base";
        throw refuse?.Invoke(message) ?? new MergeException(message);
    }
}
