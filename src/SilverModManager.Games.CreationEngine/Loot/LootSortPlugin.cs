using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Sorting;
using SilverModManager.Games.CreationEngine.SortOrder;

namespace SilverModManager.Games.CreationEngine.Loot;

/// <summary>
/// A plugin of the loadout as the sorter sees it. <paramref name="records"/> and <paramref name="assets"/> are what
/// the plugin overrides and the archive files it loads; both stay null for a game whose overlap is not read, or a
/// plugin whose bytes could not be read, and then the plugin overlaps nothing.
/// </summary>
internal sealed class LootSortPlugin(PluginLoadoutData data, PluginRecordIds? records = null, ArchiveAssetSet? assets = null) : ISortingPlugin
{
    public string Name => data.Name;
    public bool IsMaster => data.IsMaster;
    public bool IsBlueprintPlugin => data.IsBlueprint;
    public IReadOnlyList<string> Masters => data.Masters;
    public int OverrideRecordCount => records?.OverrideCount ?? 0;
    public int AssetCount => assets?.Count ?? 0;

    public bool DoRecordsOverlap(ISortingPlugin other) =>
        records is not null && other is LootSortPlugin { Records: { } theirs } && records.Overlaps(theirs);

    public bool DoAssetsOverlap(ISortingPlugin other) =>
        assets is not null && other is LootSortPlugin { Assets: { } theirs } && assets.Overlaps(theirs);

    private PluginRecordIds? Records => records;

    private ArchiveAssetSet? Assets => assets;
}
