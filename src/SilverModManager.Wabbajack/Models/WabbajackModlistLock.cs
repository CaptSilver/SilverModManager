using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Wabbajack.Models;

/// <summary>
/// A modlist's group is written with IsReadOnly = false, yet it belongs to the list, not the user: the list's page
/// installs and removes it.
/// </summary>
public sealed class WabbajackModlistLock : ICollectionLock
{
    /// <inheritdoc />
    public string? WhyLocked(CollectionGroup.ReadOnly collection)
        => collection.TryGetAsWabbajackModlistLoadoutGroup(out _)
            ? $"{collection.AsLoadoutItemGroup().AsLoadoutItem().Name} is a Wabbajack modlist; it decides what it contains"
            : null;
}
