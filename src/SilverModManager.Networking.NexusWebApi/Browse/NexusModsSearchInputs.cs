namespace SilverModManager.Networking.NexusWebApi;

/// <summary>Turns a <see cref="NexusModsSearchQuery"/> into the filter and sort the <c>mods</c> query takes.</summary>
internal static class NexusModsSearchInputs
{
    public static ModsFilter Filter(NexusModsSearchQuery query)
    {
        var filter = new ModsFilter
        {
            Op = FilterLogicalOperator.And,
            GameId = [new BaseFilterValue { Op = FilterComparisonOperator.Equals, Value = query.Game.Value.ToString() }],
        };

        // Absent, not false, when adult mods are allowed: a false clause would hide the ones the user asked to see.
        if (!query.IncludeAdult)
            filter.AdultContent = [new BooleanFilterValue { Op = FilterComparisonOperator.Equals, Value = false }];

        // A blank search sends no clause at all; an empty wildcard would match nothing useful and cost a slower query.
        var text = query.SearchText.Trim();
        if (text != "")
            filter.Name = [new BaseFilterValueEqualsWildcard { Op = FilterComparisonOperatorEqualsWildcard.Wildcard, Value = text }];

        return filter;
    }

    public static IReadOnlyList<ModsSort> Sort(NexusModsSearchQuery query)
    {
        var desc = new BaseSortValue { Direction = SortDirection.Desc };
        return query.Sort switch
        {
            NexusModsSort.Endorsements => [new ModsSort { Endorsements = desc }],
            NexusModsSort.Downloads => [new ModsSort { Downloads = desc }],
            NexusModsSort.Updated => [new ModsSort { UpdatedAt = desc }],
            NexusModsSort.Name => [new ModsSort { Name = new BaseSortValue { Direction = SortDirection.Asc } }],
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Unknown sort"),
        };
    }
}
