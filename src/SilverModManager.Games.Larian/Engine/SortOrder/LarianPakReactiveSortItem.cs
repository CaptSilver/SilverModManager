using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Games.Larian.Engine.SortOrder;

/// <summary>
/// One pak on the Load Order page.
/// </summary>
public sealed class LarianPakReactiveSortItem : IReactiveSortItem<LarianPakReactiveSortItem, SortItemKey<string>>, ISortItemDetails
{
    /// <param name="isEnabled">The pak's own switch in the sort order, which a game-installed pak does not use.</param>
    /// <param name="isAdventure">A campaign the player picks in the game: the row is locked and never on.</param>
    public LarianPakReactiveSortItem(int sortIndex, string name, bool isEnabled, LarianPakLoadoutData loadoutData, bool isAdventure = false)
    {
        IsAdventure = isAdventure;
        SortIndex = sortIndex;
        DisplayName = name;
        Key = LarianPakSortData.KeyFor(name);
        LoadoutData = loadoutData;
        ModName = loadoutData.ModName;
        ModGroupId = loadoutData.ModGroupId is { } id ? LoadoutItemGroupId.From(id) : Optional<LoadoutItemGroupId>.None;

        // The group of game-installed content decides for its paks; a stored switch on such a row is not shown.
        IsEnabled = !isAdventure && (loadoutData.IsGameInstalled ? loadoutData.IsEnabled : isEnabled);
        LockReason = isAdventure ? SortItemLockReason.AlwaysLoaded : loadoutData.IsGameInstalled ? SortItemLockReason.GameInstalledGroup : null;
        IsActive = !isAdventure && (loadoutData.IsGameInstalled ? loadoutData.IsEnabled : isEnabled && loadoutData.IsEnabled);
    }

    public SortItemKey<string> Key { get; }
    public int SortIndex { get; set; }
    public string DisplayName { get; }
    public string ModName { get; set; }
    public Optional<LoadoutItemGroupId> ModGroupId { get; set; }
    public bool IsActive { get; set; }
    public ISortItemLoadoutData? LoadoutData { get; set; }

    /// <summary>Whether the pak is a campaign, which the game chooses.</summary>
    public bool IsAdventure { get; }

    public string? Flags => IsAdventure ? "Chosen in the game" : null;
    public IReadOnlyList<string> Masters => [];

    /// <summary>Every pak can be dragged, except a campaign, which sits where the game's list has it.</summary>
    public bool IsLocked => IsAdventure;

    public bool IsEnabled { get; }

    /// <summary>A pak the game installed follows its group's switch.</summary>
    public SortItemLockReason? LockReason { get; }
}
