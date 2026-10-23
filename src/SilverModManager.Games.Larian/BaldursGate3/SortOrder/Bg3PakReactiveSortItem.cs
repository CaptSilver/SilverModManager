using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Games.Larian.BaldursGate3.SortOrder;

/// <summary>
/// One pak on the Load Order page.
/// </summary>
public sealed class Bg3PakReactiveSortItem : IReactiveSortItem<Bg3PakReactiveSortItem, SortItemKey<string>>, ISortItemDetails
{
    /// <param name="isEnabled">The pak's own switch in the sort order, which a game-installed pak does not use.</param>
    public Bg3PakReactiveSortItem(int sortIndex, string name, bool isEnabled, Bg3PakLoadoutData loadoutData)
    {
        SortIndex = sortIndex;
        DisplayName = name;
        Key = Bg3PakSortData.KeyFor(name);
        LoadoutData = loadoutData;
        ModName = loadoutData.ModName;
        ModGroupId = loadoutData.ModGroupId is { } id ? LoadoutItemGroupId.From(id) : Optional<LoadoutItemGroupId>.None;

        // The group of game-installed content decides for its paks; a stored switch on such a row is not shown.
        IsEnabled = loadoutData.IsGameInstalled ? loadoutData.IsEnabled : isEnabled;
        LockReason = loadoutData.IsGameInstalled ? SortItemLockReason.GameInstalledGroup : null;
        IsActive = loadoutData.IsGameInstalled ? loadoutData.IsEnabled : isEnabled && loadoutData.IsEnabled;
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

    /// <summary>Every pak can be dragged.</summary>
    public bool IsLocked => false;

    public bool IsEnabled { get; }

    /// <summary>A pak the game installed follows its group's switch.</summary>
    public SortItemLockReason? LockReason { get; }
}
