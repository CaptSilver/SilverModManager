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

    /// <summary>
    /// The switched-off group in the default collection that cleaning the game folder moves unknown files into, so
    /// the next Apply takes them off disk and the user can still see them and switch them back on.
    /// </summary>
    public const string FoundInGameFolderName = "Found in the game folder";

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

    /// <summary>
    /// The loadout's default collection, added to <paramref name="tx"/> when the loadout has none left. The id is
    /// that of the new collection when one was added, so it is only valid within the transaction.
    /// </summary>
    public static EntityId OfOrCreate(ITransaction tx, IDb db, LoadoutId loadoutId)
        => Of(db, loadoutId) is { } existing ? existing.Value : Create(tx, loadoutId).Id;

    private static bool IsModlist(IDb db, CollectionGroup.ReadOnly group)
    {
        foreach (var datom in db.Get(group.Id))
        {
            if (db.AttributeCache.GetSymbol(datom.A).Namespace == ModlistNamespace) return true;
        }

        return false;
    }
}
