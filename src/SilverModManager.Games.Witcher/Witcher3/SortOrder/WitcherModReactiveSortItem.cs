using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.SortOrder;

/// <summary>One Mods folder on the Load Order page.</summary>
public sealed class WitcherModReactiveSortItem : IReactiveSortItem<WitcherModReactiveSortItem, SortItemKey<string>>, ISortItemDetails
{
    public WitcherModReactiveSortItem(int sortIndex, bool isEnabled, WitcherModLoadoutData loadoutData)
    {
        SortIndex = sortIndex;
        DisplayName = loadoutData.Folder;
        Key = loadoutData.Key;
        LoadoutData = loadoutData;
        ModName = loadoutData.ModName;
        ModGroupId = loadoutData.ModGroupId;
        IsEnabled = isEnabled;
        IsActive = isEnabled && loadoutData.IsEnabled;
    }

    public SortItemKey<string> Key { get; }
    public int SortIndex { get; set; }
    public string DisplayName { get; }
    public string ModName { get; set; }
    public Optional<LoadoutItemGroupId> ModGroupId { get; set; }
    public bool IsActive { get; set; }
    public ISortItemLoadoutData? LoadoutData { get; set; }

    public string? Flags => null;
    public IReadOnlyList<string> Masters => [];

    /// <summary>The merged folder is read first whatever the user does, so it cannot be dragged or switched.</summary>
    public bool IsLocked => Key.Equals(WitcherModFolders.MergedKey);

    public bool IsEnabled { get; }

    public SortItemLockReason? LockReason => IsLocked ? SortItemLockReason.AlwaysLoaded : null;
}
