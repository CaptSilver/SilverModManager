using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>One owner other than the user that decides what a collection contains.</summary>
public interface ICollectionLock
{
    /// <summary>A plain sentence when this owner locks the collection's membership, else null.</summary>
    string? WhyLocked(CollectionGroup.ReadOnly collection);

    /// <summary>Whether the collection's rows are its provider's, linked or not, so they are never installed, uninstalled, pinned or replaced one at a time. Only a modlist says yes.</summary>
    bool OwnsItsMods(CollectionGroup.ReadOnly collection) => false;
}

/// <summary>What consumers take: every registered lock, asked in registration order.</summary>
public interface ICollectionOwnership
{
    /// <summary>The first lock's reason, or null when the user owns the collection.</summary>
    string? WhyLocked(CollectionGroup.ReadOnly collection);

    /// <summary>WhyLocked of the link's nearest collection; null when the link sits under no collection.</summary>
    string? WhyLocked(IDb db, LibraryLinkedLoadoutItem.ReadOnly link);

    /// <summary>Whether any lock says the collection owns its mod rows.</summary>
    bool OwnsItsMods(CollectionGroup.ReadOnly collection);

    /// <summary>OwnsItsMods of the link's nearest collection; false when it sits under none.</summary>
    bool OwnsLink(IDb db, LibraryLinkedLoadoutItem.ReadOnly link);
}

/// <inheritdoc />
public sealed class CollectionOwnership(IEnumerable<ICollectionLock> locks) : ICollectionOwnership
{
    /// <inheritdoc />
    public string? WhyLocked(CollectionGroup.ReadOnly collection)
        => locks.Select(l => l.WhyLocked(collection)).FirstOrDefault(static reason => reason is not null);

    /// <inheritdoc />
    public string? WhyLocked(IDb db, LibraryLinkedLoadoutItem.ReadOnly link)
        => TryNearestCollection(db, link.AsLoadoutItemGroup().AsLoadoutItem(), out var collection) ? WhyLocked(collection) : null;

    /// <inheritdoc />
    public bool OwnsItsMods(CollectionGroup.ReadOnly collection) => locks.Any(l => l.OwnsItsMods(collection));

    /// <inheritdoc />
    public bool OwnsLink(IDb db, LibraryLinkedLoadoutItem.ReadOnly link)
        => TryNearestCollection(db, link.AsLoadoutItemGroup().AsLoadoutItem(), out var collection) && OwnsItsMods(collection);

    /// <summary>The nearest CollectionGroup ancestor of item (item itself included), walking Parent only while HasParent().</summary>
    public static bool TryNearestCollection(IDb db, LoadoutItem.ReadOnly item, out CollectionGroup.ReadOnly collection)
    {
        var current = item;
        while (true)
        {
            if (current.TryGetAsLoadoutItemGroup(out var group) && group.TryGetAsCollectionGroup(out collection))
                return true;
            if (!current.HasParent() || !LoadoutItem.Parent.TryGetValue(current, out var parent)) break;
            current = LoadoutItem.Load(db, parent);
            if (!current.IsValid()) break;
        }

        collection = default;
        return false;
    }
}

/// <summary>Nexus collections are read-only: the collection's revision decides what they contain.</summary>
public sealed class NexusCollectionLock : ICollectionLock
{
    /// <inheritdoc />
    public string? WhyLocked(CollectionGroup.ReadOnly collection)
        => collection.IsReadOnly
            ? $"{collection.AsLoadoutItemGroup().AsLoadoutItem().Name} is a Nexus collection; it decides what it contains"
            : null;
}
