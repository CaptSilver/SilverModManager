namespace SilverModManager.Networking.NexusWebApi;

/// <summary>Turns a <see cref="NexusCollectionsSearchQuery"/> into the filter and sort the <c>collectionsV2</c> query takes.</summary>
internal static class NexusCollectionsSearchInputs
{
    public static CollectionsSearchFilter Filter(NexusCollectionsSearchQuery query)
    {
        var filter = new CollectionsSearchFilter
        {
            Op = FilterLogicalOperator.And,
            GameId = [new BaseFilterValue { Op = FilterComparisonOperator.Equals, Value = query.Game.Value.ToString() }],
            // A collection without a published revision can't be installed, so it has no place in the list.
            HasPublishedRevision = [new BooleanFilterValue { Op = FilterComparisonOperator.Equals, Value = true }],
        };

        // Absent, not false, when adult collections are allowed: a false clause would hide the ones the user asked to see.
        if (!query.IncludeAdult)
            filter.AdultContent = [new BooleanFilterValue { Op = FilterComparisonOperator.Equals, Value = false }];

        var text = query.SearchText.Trim();
        if (text != "")
            filter.Name = [new BaseFilterValue { Op = FilterComparisonOperator.Wildcard, Value = text }];

        return filter;
    }

    public static IReadOnlyList<CollectionsSearchSort> Sort(NexusCollectionsSearchQuery query)
    {
        var desc = new BaseSortValue { Direction = SortDirection.Desc };
        return query.Sort switch
        {
            NexusCollectionsSort.Endorsements => [new CollectionsSearchSort { Endorsements = desc }],
            NexusCollectionsSort.Downloads => [new CollectionsSearchSort { Downloads = desc }],
            NexusCollectionsSort.Updated => [new CollectionsSearchSort { UpdatedAt = desc }],
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Unknown sort"),
        };
    }
}
