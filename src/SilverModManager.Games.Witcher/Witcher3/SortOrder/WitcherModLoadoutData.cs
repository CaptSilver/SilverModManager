using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

namespace SilverModManager.Games.Witcher.Witcher3.SortOrder;

/// <summary>
/// What the loadout holds for one Mods folder: the groups that supply it, and where they rank. Folders that differ
/// only by case are one entry, spelled the way the highest-priority group spells it.
/// </summary>
public sealed class WitcherModLoadoutData : ISortItemLoadoutData<SortItemKey<string>>
{
    public WitcherModLoadoutData(string folder, ConflictPriority priority, IReadOnlyList<LoadoutItemGroupId> groups, bool isEnabled, string modName)
    {
        Folder = folder;
        Priority = priority;
        Groups = groups;
        IsEnabled = isEnabled;
        ModName = modName;
        Key = WitcherModFolders.KeyFor(folder);
        ModGroupId = groups.Count > 0 ? groups[0] : Optional<LoadoutItemGroupId>.None;
    }

    public string Folder { get; }

    /// <summary>The highest priority of the folder's groups; the minimum when none has a row.</summary>
    public ConflictPriority Priority { get; }

    /// <summary>The groups that supply the folder, highest priority first.</summary>
    public IReadOnlyList<LoadoutItemGroupId> Groups { get; }

    public SortItemKey<string> Key { get; }
    public bool IsEnabled { get; set; }

    /// <summary>The name of the highest-priority group.</summary>
    public string ModName { get; set; }

    /// <summary>The highest-priority group; none for the merged folder.</summary>
    public Optional<LoadoutItemGroupId> ModGroupId { get; set; }
}
