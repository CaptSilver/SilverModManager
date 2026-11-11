using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Library;

/// <summary>
///     Represents the properties needed for deletion of a library item.
/// </summary>
/// <param name="IsNexus">Whether the library item has been sourced from Nexus Mods (it is redownloadable).</param>
/// <param name="IsNonPermanent">
///     Whether the library item is a download that is not guaranteed to be redownloadable.
///     (As of time of writing it means 'not from Nexus Mods')
/// </param>
/// <param name="IsManuallyAdded">Whether this library item was manually added from FileSystem to library.</param>
/// <param name="Loadouts">The loadouts that hold this library item as a mod of their own (one a collection that owns its mods does not count).</param>
/// <param name="KeptByCollections">The collections that own their mods and link this library item; deleting the archive unlinks them and keeps the mods.</param>
public record struct LibraryItemRemovalInfo(bool IsNexus, bool IsNonPermanent, bool IsManuallyAdded, Loadout.ReadOnly[] Loadouts, KeptByCollection[] KeptByCollections)
{
    public static LibraryItemRemovalInfo Determine(LibraryItem.ReadOnly toRemove, Loadout.ReadOnly[] loadouts, IDb db, ICollectionOwnership ownership)
    {
        var info = new LibraryItemRemovalInfo();

        // Check if it's a file which was downloaded.
        if (toRemove.TryGetAsNexusModsLibraryItem(out _))
        {
            info.IsNexus = true;
            info.IsNonPermanent = !info.IsNexus;
        }
        else if (toRemove.TryGetAsLocalFile(out _))
        {
            info.IsManuallyAdded = true;
        }
        
        // Check if it's added to any loadout
        info.Loadouts = loadouts
            .Where(loadout => GetLoadoutItemsByLibraryItem(loadout, toRemove).Any(link => !ownership.OwnsLink(db, link)))
            .ToArray();
        info.KeptByCollections = loadouts
            .SelectMany(loadout => GetLoadoutItemsByLibraryItem(loadout, toRemove))
            .Select(link => CollectionOwnership.TryNearestCollection(db, link.AsLoadoutItemGroup().AsLoadoutItem(), out var collection) && ownership.OwnsItsMods(collection)
                ? new KeptByCollection(collection.Id, collection.AsLoadoutItemGroup().AsLoadoutItem().Name)
                : (KeptByCollection?)null)
            .OfType<KeptByCollection>()
            .Distinct()
            .ToArray();
        return info;
    }

    /// <summary>
    /// Returns an enumerable containing all loadout items linked to the given library item.
    /// </summary>
    private static IEnumerable<LibraryLinkedLoadoutItem.ReadOnly> GetLoadoutItemsByLibraryItem(Loadout.ReadOnly loadout, LibraryItem.ReadOnly libraryItem)
    {
        // Start with a backref. This assumes that the number of loadouts with a given library item will be fairly small.
        // This could be false, but it's a good starting point.
        return LibraryLinkedLoadoutItem
            .FindByLibraryItem(loadout.Db, libraryItem)
            .Where(linked => linked.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout);
    }
}

/// <summary>A collection that keeps its mods when a library item it links is deleted. The id tells two collections of the same name apart.</summary>
public readonly record struct KeptByCollection(EntityId Id, string Name);
