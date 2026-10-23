using SilverModManager.Games.Bethesda.Loot.Metadata;

namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/plugins.rs (PluginSortingData).

/// <summary>
/// A plugin together with the masterlist and user metadata that affects where it sorts.
/// </summary>
public sealed class PluginSortingData<T> where T : ISortingPlugin
{
    public PluginSortingData(T plugin, PluginMetadata? masterlistMetadata, PluginMetadata? userMetadata, int loadOrderIndex)
    {
        Plugin = plugin;
        IsMaster = plugin.IsMaster;
        OverrideRecordCount = plugin.OverrideRecordCount;
        LoadOrderIndex = loadOrderIndex;

        Group = userMetadata?.Group ?? masterlistMetadata?.Group ?? Metadata.Group.DefaultName;
        GroupIsUserMetadata = userMetadata?.Group is not null;
        MasterlistLoadAfter = ToFilenames(masterlistMetadata?.LoadAfter);
        UserLoadAfter = ToFilenames(userMetadata?.LoadAfter);
        MasterlistRequirements = ToFilenames(masterlistMetadata?.Requirements);
        UserRequirements = ToFilenames(userMetadata?.Requirements);
    }

    public T Plugin { get; }

    public string Name => Plugin.Name;

    public bool IsMaster { get; internal set; }

    public int OverrideRecordCount { get; internal set; }

    public int LoadOrderIndex { get; }

    public string Group { get; internal set; }

    public bool GroupIsUserMetadata { get; }

    public IReadOnlyList<string> MasterlistLoadAfter { get; internal set; }

    public IReadOnlyList<string> UserLoadAfter { get; internal set; }

    public IReadOnlyList<string> MasterlistRequirements { get; internal set; }

    public IReadOnlyList<string> UserRequirements { get; internal set; }

    public bool IsBlueprintMaster => IsMaster && Plugin.IsBlueprintPlugin;

    public int AssetCount => Plugin.AssetCount;

    public IReadOnlyList<string> Masters => Plugin.Masters;

    public bool DoRecordsOverlap(PluginSortingData<T> other) => Plugin.DoRecordsOverlap(other.Plugin);

    public bool DoAssetsOverlap(PluginSortingData<T> other) => Plugin.DoAssetsOverlap(other.Plugin);

    private static string[] ToFilenames(IEnumerable<Metadata.File>? files) =>
        files?.Select(f => f.Name.Value).ToArray() ?? [];
}
