namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>One owner other than the user that decides what a collection contains.</summary>
public interface ICollectionLock
{
    /// <summary>A plain sentence when this owner locks the collection's membership, else null.</summary>
    string? WhyLocked(CollectionGroup.ReadOnly collection);
}

/// <summary>What consumers take: every registered lock, asked in registration order.</summary>
public interface ICollectionOwnership
{
    /// <summary>The first lock's reason, or null when the user owns the collection.</summary>
    string? WhyLocked(CollectionGroup.ReadOnly collection);
}

/// <inheritdoc />
public sealed class CollectionOwnership(IEnumerable<ICollectionLock> locks) : ICollectionOwnership
{
    /// <inheritdoc />
    public string? WhyLocked(CollectionGroup.ReadOnly collection)
        => locks.Select(l => l.WhyLocked(collection)).FirstOrDefault(static reason => reason is not null);
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
