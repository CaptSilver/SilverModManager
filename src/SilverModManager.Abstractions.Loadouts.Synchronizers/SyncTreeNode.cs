using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// A pairing of a path and a sync node part.
/// </summary>
public readonly record struct PathPartPair(GamePath Path, SyncNodePart Part);

/// <summary>
/// A pairing of a path and a sync tree node.
/// </summary>
public readonly record struct PathNodePair(GamePath Path, SyncNode Node);

/// <summary>
/// A grouping of (optional) entityId, identity, size and modified time. 8 bytes for the entity id, size and time, and 34 for the
/// identity (kind, length and a 32 byte digest buffer), so about 56 bytes with padding.
/// </summary>
public readonly struct SyncNodePart
{
    /// <summary>
    /// Default constructor.
    /// </summary>
    public SyncNodePart()
    {
        EntityId = EntityId.From(0);
        Identity = FileIdentity.None;
        Size = Size.Zero;
    }
    
    /// <summary>
    /// An optional entity ID that links to where this data came from. For loadout parts
    /// this will be the loadout item. For DiskState parts this will be the disk state entry.
    /// </summary>
    public EntityId EntityId { get; init; }
    
    /// <summary>
    /// The identity of the file: a hash kind and its digest. <see cref="FileIdentity.None"/> when this part is absent.
    /// Loadout parts are always xxHash3; a disk or previous part may carry another kind.
    /// </summary>
    public required FileIdentity Identity { get; init; }
    
    /// <summary>
    /// The size of the file.
    /// </summary>
    public required Size Size { get; init; }
    
    /// <summary>
    /// The last modified time of the file in UTC ticks.
    /// </summary>
    public required long LastModifiedTicks { get; init; }

    /// <summary>
    /// True if the entity ID has a value.
    /// </summary>
    public bool HaveEntityId => EntityId.Value != 0;
}

/// <summary>
/// A node in the synchronization tree. About 56 bytes for each part, and two ushorts for the other fields. So about 170 bytes total.
/// </summary>
public record struct SyncNode
{
    /// <summary>
    /// The current disk state
    /// </summary>
    public SyncNodePart Disk { get; set; }

    /// <summary>
    /// The previously applied state
    /// </summary>
    public SyncNodePart Previous { get; set; }
    
    /// <summary>
    /// The loadout state
    /// </summary>
    public SyncNodePart Loadout { get; set; }

    /// <summary>
    /// True if the disk state is present.
    /// </summary>
    public bool HaveDisk => !Disk.Identity.IsNone;
    
    /// <summary>
    /// True if the previous state is present.
    /// </summary>
    public bool HavePrevious => !Previous.Identity.IsNone;
    
    /// <summary>
    /// True if the loadout state is present.
    /// </summary>
    public bool HaveLoadout => !Loadout.Identity.IsNone;
    
    /// <summary>
    /// The type of thing that the data from the loadout came from. Game state, deleted file, loadout file, etc.
    /// </summary>
    public LoadoutSourceItemType SourceItemType { get; set; }
    
    /// <summary>
    /// Sync state signature.
    /// </summary>
    public Signature Signature { get; set; }
    
    /// <summary>
    /// Actions that can be performed on this node.
    /// </summary>
    public Actions Actions { get; set; }
}
