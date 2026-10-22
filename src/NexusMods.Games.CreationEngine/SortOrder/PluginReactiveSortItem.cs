using DynamicData.Kernel;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.SortOrder;

/// <summary>
/// One plugin on the Load Order page.
/// </summary>
public sealed class PluginReactiveSortItem : IReactiveSortItem<PluginReactiveSortItem, SortItemKey<string>>, ISortItemDetails
{
    /// <param name="isEnabled">The plugin's own switch in the sort order.</param>
    public PluginReactiveSortItem(int sortIndex, string name, bool isEnabled, PluginLoadoutData loadoutData)
    {
        SortIndex = sortIndex;
        DisplayName = name;
        Key = PluginSortData.KeyFor(name);
        IsEnabled = isEnabled;
        LoadoutData = loadoutData;
        ModName = loadoutData.ModName;
        ModGroupId = loadoutData.ModGroupId is { } id ? LoadoutItemGroupId.From(id) : Optional<LoadoutItemGroupId>.None;
        Masters = loadoutData.Masters;
        Flags = loadoutData.Flags;
        IsLocked = loadoutData.EarlyLoaderIndex is not null;
        // Active means the game will load it: switched on here, and the mod that supplies it is enabled too.
        IsActive = isEnabled && loadoutData.IsEnabled;
    }

    public SortItemKey<string> Key { get; }
    public int SortIndex { get; set; }
    public string DisplayName { get; }
    public string ModName { get; set; }
    public Optional<LoadoutItemGroupId> ModGroupId { get; set; }
    public bool IsActive { get; set; }
    public ISortItemLoadoutData? LoadoutData { get; set; }

    public string? Flags { get; }
    public IReadOnlyList<string> Masters { get; }
    public bool IsLocked { get; }
    public bool IsEnabled { get; }
}
