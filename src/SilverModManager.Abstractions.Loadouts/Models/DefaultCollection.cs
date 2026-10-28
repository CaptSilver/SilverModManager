using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// The collection a loadout puts the user's own mods in ("My Mods").
/// </summary>
public static class DefaultCollection
{
    /// <summary>The name a loadout's default collection is created with.</summary>
    public const string Name = "My Mods";

    // A Wabbajack modlist's group is a writable collection too, but it is not where the user's own mods go. Its model lives
    // in a project this one can't reference, so it is recognised by the attributes it carries.
    private const string ModlistNamespace = "SilverModManager.Wabbajack.ModlistLoadoutGroup";

    /// <summary>
    /// The loadout's default collection: the oldest one the user can edit and that is not an imported modlist. The
    /// loadout is created with it. Null when the loadout has none left (it can be deleted once another editable
    /// collection exists, after which no collection is pinned).
    /// </summary>
    public static CollectionGroupId? Of(IDb db, LoadoutId loadoutId)
    {
        return CollectionGroup.All(db)
            .Where(group => !group.IsReadOnly
                && group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadoutId
                && !IsModlist(db, group))
            .OrderBy(group => group.Id)
            .Select(group => (CollectionGroupId?)group.CollectionGroupId)
            .FirstOrDefault();
    }

    /// <summary>Adds a new default collection for the loadout to <paramref name="tx"/>.</summary>
    public static CollectionGroup.New Create(ITransaction tx, LoadoutId loadoutId)
    {
        return new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = Name,
                    LoadoutId = loadoutId,
                },
            },
        };
    }

    private static bool IsModlist(IDb db, CollectionGroup.ReadOnly group)
    {
        foreach (var datom in db.Get(group.Id))
        {
            if (db.AttributeCache.GetSymbol(datom.A).Namespace == ModlistNamespace) return true;
        }

        return false;
    }
}
