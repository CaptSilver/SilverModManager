using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Abstractions.Library;

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
    /// Puts the library item into the loadout the way an install needs it: installs it when it has no group, enables
    /// its group when that is disabled, and leaves it alone otherwise. A disabled dependency is not installed again,
    /// but a mod that needs it would be left with a dependency that is not in the game.
    /// </summary>
    /// <param name="install">Runs the install when the item has no group in the loadout.</param>
    /// <exception cref="ThunderstorePackageInDisabledGroupException">
    /// Every group of the item sits in a disabled parent, such as a disabled collection.
    /// </exception>
    public static async Task<LoadoutPackageOutcome> InstallOrEnable(IConnection connection, LibraryItem.ReadOnly item, LoadoutId loadoutId, Func<Task> install)
    {
        var links = LibraryLinkedLoadoutItem.FindByLibraryItem(connection.Db, item)
            .Where(link => link.AsLoadoutItem().LoadoutId == loadoutId)
            .Select(link => link.AsLoadoutItem())
            .ToArray();
        if (links.Length == 0)
        {
            await install();
            return LoadoutPackageOutcome.Installed;
        }

        if (links.Any(link => link.IsEnabled())) return LoadoutPackageOutcome.AlreadyEnabled;

        // Only the item's own group is enabled. Enabling the disabled collection around it would also switch on
        // every other mod the user turned off there, so that is left to the user.
        var enableable = links.Where(link => ParentIsEnabled(connection.Db, link)).ToArray();
        if (enableable.Length == 0)
            throw new ThunderstorePackageInDisabledGroupException(item.Name, FirstDisabledParent(connection.Db, links[0]).Name);

        using var tx = connection.BeginTransaction();
        tx.Retract(enableable[0].Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
        return LoadoutPackageOutcome.Enabled;
    }

    private static bool ParentIsEnabled(IDb db, LoadoutItem.ReadOnly item)
        => !LoadoutItem.Parent.TryGetValue(item, out var parentId) || LoadoutItem.Load(db, parentId).IsEnabled();

    private static LoadoutItem.ReadOnly FirstDisabledParent(IDb db, LoadoutItem.ReadOnly item)
    {
        var current = item;
        while (LoadoutItem.Parent.TryGetValue(current, out var parentId))
        {
            current = LoadoutItem.Load(db, parentId);
            if (current.IsDisabled) return current;
        }

        throw new InvalidOperationException($"{item.Name} has no disabled parent group.");
    }
}

/// <summary>
/// A Thunderstore package the install needs is in the loadout, but inside a disabled group the install will not
/// enable on its own.
/// </summary>
public sealed class ThunderstorePackageInDisabledGroupException(string package, string group)
    : Exception($"{package} is in the loadout, but '{group}', the group holding it, is disabled. Enable '{group}' and install again.");

/// <summary>
/// What <see cref="ThunderstoreLoadoutPackages.InstallOrEnable"/> did for one library item.
/// </summary>
public enum LoadoutPackageOutcome
{
    Installed,
    Enabled,
    AlreadyEnabled,
}
