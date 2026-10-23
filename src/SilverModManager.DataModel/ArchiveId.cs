using TransparentValueObjects;

namespace SilverModManager.DataModel;

/// <summary>
/// A unique identifier for an archive in a ArchiveManager
/// </summary>
[ValueObject<Guid>]
public readonly partial struct ArchiveId { }
