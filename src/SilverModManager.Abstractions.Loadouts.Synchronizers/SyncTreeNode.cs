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
public readonly struct SyncNodePart : IEquatable<SyncNodePart>
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

    /// <inheritdoc/>
    public bool Equals(SyncNodePart other) =>
        EntityId.Equals(other.EntityId)
        && Identity.Equals(other.Identity)
        && Size.Equals(other.Size)
        && LastModifiedTicks == other.LastModifiedTicks;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SyncNodePart other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(EntityId, Identity, Size, LastModifiedTicks);
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
    /// Whether the node has a file once this sync is done. The loadout side alone is not that: a contributor whose
    /// only file is merged is deleted from disk, and a generated file enters the tree with no loadout side at all.
    /// </summary>
    public bool WillBeOnDisk
        => Actions.HasFlag(Actions.ExtractToDisk)
           || Actions.HasFlag(Actions.WriteIntrinsic)
           || (HaveDisk && !Actions.HasFlag(Actions.DeleteFromDisk));

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

    /// <summary>
    /// True when the file was applied before, is gone from disk, and the row that provides it is the player's, not a
    /// mod's: an Overrides row, or a Game row of an install the store's file list does not cover. Only set for a
    /// node that is missing from disk but has a previous entry.
    /// </summary>
    public bool PlayerDeleted { get; set; }

    /// <summary>
    /// True when two present parts carry identities of different kinds and no recorded digest converts one into the
    /// other, so whether they name the same bytes is not known until the file is read.
    /// </summary>
    public bool IdentityUnresolved { get; set; }

    /// <summary>
    /// True when a read gave the disk part its xxHash3 in this tree but the recorded disk state still names the file
    /// by the identity it had. The apply writes the new identity to that entry.
    /// </summary>
    public bool DiskNamedByRead { get; set; }
}
