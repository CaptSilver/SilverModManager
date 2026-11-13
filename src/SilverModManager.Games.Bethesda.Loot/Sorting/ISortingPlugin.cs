namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (SortingPlugin).

/// <summary>What the sorter needs to know about a plugin.</summary>
public interface ISortingPlugin
{
    string Name { get; }

    bool IsMaster { get; }

    bool IsBlueprintPlugin { get; }

    IReadOnlyList<string> Masters { get; }

    int OverrideRecordCount { get; }

    int AssetCount { get; }

    bool DoRecordsOverlap(ISortingPlugin other);

    bool DoAssetsOverlap(ISortingPlugin other);
}

/// <summary>
/// What a plugin can offer so that the sorter looks only at pairs that might overlap, instead of every pair. Two
/// plugins whose <see cref="ISortingPlugin.DoRecordsOverlap"/> answers true must share a record key, and likewise
/// for assets; a key two plugins share without overlapping only costs a comparison.
/// </summary>
public interface IOverlapKeys
{
    IReadOnlyList<ulong> RecordKeys { get; }

    IReadOnlyList<ulong> AssetKeys { get; }
}
