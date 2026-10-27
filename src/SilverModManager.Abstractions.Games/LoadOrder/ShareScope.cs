using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>What happens to a stored rule when a collection is shared.</summary>
public enum RuleShare
{
    /// <summary>Neither end is in the collection, so sharing it says nothing about the rule.</summary>
    Unrelated,

    /// <summary>Both ends are shared mods; the rule goes into the manifest.</summary>
    Written,

    /// <summary>An end is a mod of the collection that the manifest cannot name.</summary>
    UnnameableEnd,

    /// <summary>An end is a group outside the collection.</summary>
    OutsideEnd,
}

/// <summary>
/// Which children of a collection a share writes out and which it leaves behind, and what that does to the stored rules.
/// CollectionCreator writes from it and the Health Check cards read from it, so the two cannot disagree.
/// </summary>
public sealed class ShareScope
{
    private readonly HashSet<LoadoutItemGroupId> _shared;
    private readonly HashSet<LoadoutItemGroupId> _children;

    private ShareScope(
        IReadOnlyList<LibraryLinkedLoadoutItem.ReadOnly> shared,
        IReadOnlyList<LoadoutItemGroup.ReadOnly> skipped,
        HashSet<LoadoutItemGroupId> children)
    {
        Shared = shared;
        Skipped = skipped;
        _shared = shared.Select(child => child.AsLoadoutItemGroup().LoadoutItemGroupId).ToHashSet();
        _children = children;
    }

    /// <summary>The children a manifest can name, winner first (a child with no priority row sorts last).</summary>
    public IReadOnlyList<LibraryLinkedLoadoutItem.ReadOnly> Shared { get; }

    /// <summary>The library-linked children a manifest cannot name, in the same order.</summary>
    public IReadOnlyList<LoadoutItemGroup.ReadOnly> Skipped { get; }

    public static ShareScope Of(LoadoutItemGroup.ReadOnly collection)
    {
        var loadout = collection.AsLoadoutItem().LoadoutId;
        var priorities = LoadoutItemGroupPriority.FindByLoadout(collection.Db, loadout)
            .ToDictionary(row => row.TargetId, row => row.Priority.Value);

        var groups = collection.Children.OfTypeLoadoutItemGroup().ToArray();
        var children = groups.OfTypeLibraryLinkedLoadoutItem()
            .OrderByDescending(child => priorities.TryGetValue(child.AsLoadoutItemGroup().LoadoutItemGroupId, out var priority) ? (long)priority : -1L)
            .ThenBy(child => child.Id.Value)
            .ToArray();

        var shared = new List<LibraryLinkedLoadoutItem.ReadOnly>();
        var skipped = new List<LoadoutItemGroup.ReadOnly>();
        foreach (var child in children)
        {
            if (ModOrderMatcher.CanIdentify(child)) shared.Add(child);
            else skipped.Add(child.AsLoadoutItemGroup());
        }

        return new ShareScope(shared, skipped, groups.Select(group => group.LoadoutItemGroupId).ToHashSet());
    }

    /// <summary>The verdict a share gives <paramref name="rule"/>.</summary>
    public RuleShare Classify(LoadoutGroupRule.ReadOnly rule)
    {
        var source = rule.SourceId;
        var other = rule.OtherId;
        if (!_children.Contains(source) && !_children.Contains(other)) return RuleShare.Unrelated;
        if (_shared.Contains(source) && _shared.Contains(other)) return RuleShare.Written;

        // A mod of this collection that cannot be named is the more useful reason when both apply.
        var unnameable = (_children.Contains(source) && !_shared.Contains(source)) || (_children.Contains(other) && !_shared.Contains(other));
        return unnameable ? RuleShare.UnnameableEnd : RuleShare.OutsideEnd;
    }
}
