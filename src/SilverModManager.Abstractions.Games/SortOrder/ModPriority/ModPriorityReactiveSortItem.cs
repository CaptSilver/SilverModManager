using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Abstractions.Games;

/// <summary>A mod's priority row as a line of the Load Order page. The key is the row's entity id.</summary>
public class ModPriorityReactiveSortItem : IReactiveSortItem<ModPriorityReactiveSortItem, SortItemKey<EntityId>>
{
    public ModPriorityReactiveSortItem(int sortIndex, EntityId priorityRow, LoadoutItemGroupId group, string modName, bool isActive)
    {
        SortIndex = sortIndex;
        Key = new SortItemKey<EntityId>(priorityRow);
        DisplayName = modName;
        ModName = modName;
        ModGroupId = group;
        IsActive = isActive;
        LoadoutData = new SortItemLoadoutData<SortItemKey<EntityId>>(Key, isActive, modName, group);
    }

    public SortItemKey<EntityId> Key { get; }

    public int SortIndex { get; set; }
    public string DisplayName { get; }
    public string ModName { get; set; }
    public Optional<LoadoutItemGroupId> ModGroupId { get; set; }
    public bool IsActive { get; set; }
    public ISortItemLoadoutData? LoadoutData { get; set; }
}
