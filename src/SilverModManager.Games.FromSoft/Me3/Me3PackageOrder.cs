namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>
/// The order packages are listed in the me3 profile. me3 lets the last package that provides a path win, so the last
/// one here is the loadout's winner; the merge index asks the same question to name the winner of a path it doesn't
/// merge, and shares this so the two cannot drift.
/// </summary>
public static class Me3PackageOrder
{
    /// <summary>
    /// Ascending priority. A package with no entry follows those with one, and ties and unlisted packages fall back to the id.
    /// </summary>
    public static List<string> Ascending(IEnumerable<string> packages, IReadOnlyDictionary<string, ulong>? priorities)
        => packages
            .OrderBy(id => priorities is not null && priorities.ContainsKey(id) ? 0 : 1)
            .ThenBy(id => priorities is not null && priorities.TryGetValue(id, out var priority) ? priority : 0UL)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
}
