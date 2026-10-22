using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Abstractions.Library;

/// <summary>
/// What a loadout already holds of Thunderstore, as the install request and the install loops need it.
/// </summary>
public static class ThunderstoreLoadoutPackages
{
    /// <summary>
    /// The Thunderstore packages in the loadout, with the highest version of each. Only enabled groups count: a
    /// disabled mod is not in the game, so its dependents still need what it would have brought.
    /// </summary>
    public static Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber> Installed(IDb db, LoadoutId loadoutId)
    {
        var installed = new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>();
        foreach (var link in LibraryLinkedLoadoutItem.All(db))
        {
            var item = link.AsLoadoutItem();
            if (item.LoadoutId != loadoutId || !item.IsEnabled()) continue;
            if (!link.LibraryItem.TryGetAsLibraryFile(out var file) || !file.TryGetAsThunderstoreLibraryFile(out var package)) continue;
            if (!ThunderstorePackageRef.TryCreate(package.Namespace, package.Name, out var reference)) continue;
            if (!ThunderstoreVersionNumber.TryParse(package.VersionNumber, out var version)) continue;

            if (!installed.TryGetValue(reference, out var other) || version > other) installed[reference] = version;
        }

        return installed;
    }

    /// <summary>
    /// Whether the library item already has a group in the loadout, enabled or not.
    /// </summary>
    public static bool IsLinked(IDb db, LibraryItem.ReadOnly item, LoadoutId loadoutId)
        => LibraryLinkedLoadoutItem.FindByLibraryItem(db, item).Any(link => link.AsLoadoutItem().LoadoutId == loadoutId);
}
