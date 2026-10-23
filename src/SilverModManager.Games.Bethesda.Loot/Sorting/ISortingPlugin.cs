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
