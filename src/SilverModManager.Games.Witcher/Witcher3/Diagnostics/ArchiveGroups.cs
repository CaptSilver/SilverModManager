using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Diagnostics;

/// <summary>The loadout's groups that were installed from a library archive, as the two Witcher 3 archive cards read them.</summary>
internal static class ArchiveGroups
{
    public static IEnumerable<(LibraryLinkedLoadoutItem.ReadOnly Group, LibraryArchive.ReadOnly Archive)> Of(Loadout.ReadOnly loadout)
    {
        foreach (var group in LoadoutItem.FindByLoadout(loadout.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().OfTypeLibraryLinkedLoadoutItem())
        {
            if (group.LibraryItem.TryGetAsLibraryFile(out var file) && file.TryGetAsLibraryArchive(out var archive))
                yield return (group, archive);
        }
    }
}
