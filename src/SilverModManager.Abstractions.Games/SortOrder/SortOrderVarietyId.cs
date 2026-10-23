using JetBrains.Annotations;
using TransparentValueObjects;

namespace SilverModManager.Abstractions.Games;


/// <summary>
/// Represents a unique identifier for a sort order variety.
/// </summary>
[PublicAPI]
[ValueObject<Guid>]
public readonly partial struct SortOrderVarietyId { }
