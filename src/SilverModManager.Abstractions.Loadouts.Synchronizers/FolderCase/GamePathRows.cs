using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;

/// <summary>A row holding a game path: its entity, the parent reference stored with the path, and the path as text.</summary>
internal readonly record struct PathRow(EntityId Row, EntityId Parent, LocationId Location, string Path)
{
    public (EntityId, LocationId, NexusMods.Paths.RelativePath) Tuple => (Parent, Location, NexusMods.Paths.RelativePath.FromUnsanitizedInput(Path));
    public GamePath GamePath => new(Location, NexusMods.Paths.RelativePath.FromUnsanitizedInput(Path));
}

/// <summary>
/// Fast reads of the rows that record a game path, each a sorted merge join: the rows of one owner come from the owner's
/// reference attribute (indexed by value), and the path of each is found by fast-forwarding a scan of the path
/// attribute to the row. Nothing loads an entity, so 280k rows read in one pass.
/// </summary>
internal static class GamePathRows
{
    /// <summary>The target paths of every item in the loadout.</summary>
    public static List<PathRow> TargetPaths(IDb db, EntityId loadout)
        => Read(db, LoadoutItem.Loadout, loadout, LoadoutItemWithTargetPath.TargetPath);

    /// <summary>The paths pinned to a winner in the loadout.</summary>
    public static List<PathRow> PinPaths(IDb db, EntityId loadout)
        => Read(db, LoadoutFileWinner.Loadout, loadout, LoadoutFileWinner.TargetPath);

    /// <summary>The paths of the game's own files the loadout records.</summary>
    public static List<PathRow> GameInstalledFilePaths(IDb db, EntityId loadout)
        => Read(db, LoadoutItem.Loadout, loadout, GameInstalledFile.Path);

    /// <summary>The paths of the claims on game-installed files.</summary>
    public static List<PathRow> GameInstalledClaimPaths(IDb db, EntityId loadout)
        => Read(db, GameInstalledClaim.Loadout, loadout, GameInstalledClaim.Path);

    /// <summary>The recorded disk state of the game.</summary>
    public static List<PathRow> DiskStatePaths(IDb db, EntityId metadata)
        => Read(db, DiskStateEntry.Game, metadata, DiskStateEntry.Path);

    private static List<PathRow> Read(IDb db, IAttribute owner, EntityId ownerValue, GamePathParentAttribute path)
    {
        var rows = new List<PathRow>();
        var cache = db.AttributeCache;
        var ownerId = cache.GetAttributeId(owner.Id);
        var pathId = cache.GetAttributeId(path.Id);

        using var owned = db.LightweightDatoms(SliceDescriptor.Create(ownerId, ownerValue));
        using var paths = db.LightweightDatoms(SliceDescriptor.Create(pathId));
        var parentSize = Unsafe.SizeOf<EntityId>();
        var locationSize = Unsafe.SizeOf<LocationId>();
        while (owned.MoveNext())
        {
            var entity = owned.KeyPrefix.E;
            if (!paths.FastForwardTo(entity)) continue;

            var value = paths.ValueSpan;
            var parent = MemoryMarshal.Read<EntityId>(value);
            var location = MemoryMarshal.Read<LocationId>(value[parentSize..]);
            rows.Add(new PathRow(entity, parent, location, Encoding.UTF8.GetString(value[(parentSize + locationSize)..])));
        }
        return rows;
    }
}
