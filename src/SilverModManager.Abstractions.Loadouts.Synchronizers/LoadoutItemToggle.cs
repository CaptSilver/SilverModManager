using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Which loadout items the user can switch on and off. The loadout page, the terminal and the diagnostics actions
/// all follow this one rule.
/// </summary>
public static class LoadoutItemToggle
{
    /// <summary>
    /// True when the item's switch cannot be used: a required collection item, or content the game installed
    /// (its items and files follow the group's switch).
    /// </summary>
    public static bool IsToggleLocked<T>(T entity) where T : struct, IReadOnlyModel<T>
    {
        return IsRequiredCollectionItem(entity) || GameInstalledGroup.IsInstalledContent(entity);
    }

    /// <summary>
    /// Why the item's switch cannot be used right now, or null when it can. Beyond <see cref="IsToggleLocked{T}"/>,
    /// an item inside a collection that is switched off follows the collection.
    /// </summary>
    public static string? SkipReason(LoadoutItem.ReadOnly item)
    {
        if (IsRequiredCollectionItem(item)) return "it is a required item of a collection";
        if (GameInstalledGroup.IsInstalledContent(item)) return "the game installed it, so it follows the Installed by the game group";
        if (item.HasParent() && item.Parent.TryGetAsCollectionGroup(out var collection) && collection.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled)
            return $"its collection {collection.AsLoadoutItemGroup().AsLoadoutItem().Name} is switched off";
        return null;
    }

    private static bool IsRequiredCollectionItem<T>(T entity) where T : struct, IReadOnlyModel<T>
    {
        return NexusCollectionItemLoadoutGroup.IsRequired.TryGetValue(entity, out var required) && required;
    }
}
