using System.Runtime.InteropServices;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;

/// <summary>A file's game path, as text, with the loadout item (the mod's group) that holds the file.</summary>
public readonly record struct OwnedFilePath(EntityId Owner, string Path);

/// <summary>
/// Reads every file path of a loadout together with the group that holds it, in one pass over the database and
/// without loading an entity, so a loadout of 280k files reads in about the time <see cref="GamePathRows"/> takes.
/// </summary>
public static class LoadoutFilePaths
{
    public static List<OwnedFilePath> Read(IDb db, LoadoutId loadout)
    {
        var rows = GamePathRows.TargetPaths(db, loadout.Value);
        var owned = new List<OwnedFilePath>(rows.Count);
        if (rows.Count == 0) return owned;

        var parentId = db.AttributeCache.GetAttributeId(LoadoutItem.ParentId.Id);
        using var parents = db.LightweightDatoms(SliceDescriptor.Create(parentId));

        // Rows come out in entity order, which is the order the parent attribute is scanned in.
        foreach (var row in rows)
        {
            if (parents.FastForwardTo(row.Row) is false) continue;
            owned.Add(new OwnedFilePath(MemoryMarshal.Read<EntityId>(parents.ValueSpan), row.Path));
        }

        return owned;
    }
}
