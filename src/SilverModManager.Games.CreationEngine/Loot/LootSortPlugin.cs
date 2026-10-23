using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.Loot.Sorting;
using NexusMods.Games.CreationEngine.SortOrder;

namespace NexusMods.Games.CreationEngine.Loot;

/// <summary>
/// A plugin of the loadout as the sorter sees it. Nothing here reads record or asset data yet, so no two plugins
/// are reported to overlap and the sorter adds no overlap edges.
/// </summary>
internal sealed class LootSortPlugin(PluginLoadoutData data) : ISortingPlugin
{
    public string Name => data.Name;
    public bool IsMaster => data.IsMaster;
    public bool IsBlueprintPlugin => data.IsBlueprint;
    public IReadOnlyList<string> Masters => data.Masters;
    public int OverrideRecordCount => 0;
    public int AssetCount => 0;
    public bool DoRecordsOverlap(ISortingPlugin other) => false;
    public bool DoAssetsOverlap(ISortingPlugin other) => false;
}
