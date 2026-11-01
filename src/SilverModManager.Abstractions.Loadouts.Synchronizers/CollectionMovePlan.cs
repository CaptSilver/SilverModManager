using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>An item the plan will not move, with the sentence that says why.</summary>
public sealed record CollectionMoveSkip(LoadoutItemId Item, string Name, string Reason);

/// <summary>
/// Which of the dragged items can move into a collection. Pure: it reads the database and decides, so the drop
/// target's caption and the move itself apply the same rules.
/// </summary>
public sealed class CollectionMovePlan
{
    private CollectionMovePlan(string targetName, IReadOnlyList<LoadoutItemGroupId> movable, IReadOnlyList<CollectionMoveSkip> skipped)
    {
        TargetName = targetName;
        Movable = movable;
        Skipped = skipped;
    }

    /// <summary>The mod groups that move, in the order they were given.</summary>
    public IReadOnlyList<LoadoutItemGroupId> Movable { get; }

    /// <summary>The items that stay, in the order they were given.</summary>
    public IReadOnlyList<CollectionMoveSkip> Skipped { get; }

    /// <summary>The target collection's name.</summary>
    public string TargetName { get; }

    /// <summary>
    /// One sentence for when nothing can move: the first reason when they all share it, else
    /// "None of these mods can move to {TargetName}". Empty when something can move.
    /// </summary>
    public string RefusalText
    {
        get
        {
            if (Movable.Count > 0) return string.Empty;
            if (Skipped.Count > 0 && Skipped.All(skip => skip.Reason == Skipped[0].Reason)) return Skipped[0].Reason;
            return $"None of these mods can move to {TargetName}";
        }
    }

    /// <summary>
    /// Sorts <paramref name="items"/> into what can move into <paramref name="target"/> and what can't. The first rule
    /// an item fails names its reason.
    /// </summary>
    public static CollectionMovePlan Plan(IDb db, IReadOnlyList<LoadoutItemId> items, CollectionGroupId target, ICollectionOwnership ownership)
    {
        var targetGroup = CollectionGroup.Load(db, target);
        var targetItem = targetGroup.AsLoadoutItemGroup().AsLoadoutItem();
        var targetName = targetItem.Name;
        var targetLock = ownership.WhyLocked(targetGroup);

        var movable = new List<LoadoutItemGroupId>();
        var skipped = new List<CollectionMoveSkip>();
        foreach (var id in items.Distinct())
        {
            var item = LoadoutItem.Load(db, id);
            if (item.IsValid() is false)
            {
                skipped.Add(new CollectionMoveSkip(id, string.Empty, "This item no longer exists"));
                continue;
            }

            var reason = targetLock ?? WhyStays(db, item, targetItem, targetName, ownership);
            if (reason is null) movable.Add(LoadoutItemGroupId.From(id.Value));
            else skipped.Add(new CollectionMoveSkip(id, item.Name, reason));
        }

        return new CollectionMovePlan(targetName, movable, skipped);
    }

    private static string? WhyStays(IDb db, LoadoutItem.ReadOnly item, LoadoutItem.ReadOnly target, string targetName, ICollectionOwnership ownership)
    {
        if (item.LoadoutId != target.LoadoutId) return $"{item.Name} belongs to another loadout";

        var parentCollection = ParentCollection(db, item);
        if (parentCollection is not null && item.ParentId.Value == target.Id) return $"{item.Name} is already in {targetName}";

        if (parentCollection is { } from && ownership.WhyLocked(from) is not null)
        {
            var sourceName = from.AsLoadoutItemGroup().AsLoadoutItem().Name;
            var advice = from.IsReadOnly ? "; make an editable copy of it first" : string.Empty;
            return $"{item.Name} belongs to {sourceName}, which decides what it contains{advice}";
        }

        if (GameInstalledGroup.InstalledFor.Contains(item)) return $"{item.Name} is part of the game's own content";

        var isModInCollection = LoadoutItemGroup.Group.Contains(item) && parentCollection is not null;
        return isModInCollection ? null : $"{item.Name} is not a mod";
    }

    /// <summary>Parent is optional, and reading it on an item without one throws.</summary>
    private static CollectionGroup.ReadOnly? ParentCollection(IDb db, LoadoutItem.ReadOnly item)
    {
        if (item.HasParent() is false) return null;
        var parent = CollectionGroup.Load(db, item.ParentId.Value);
        return parent.IsValid() ? parent : null;
    }
}
