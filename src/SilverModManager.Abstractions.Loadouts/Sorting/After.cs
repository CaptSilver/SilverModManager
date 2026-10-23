using SilverModManager.Abstractions.Serialization.Attributes;

namespace SilverModManager.Abstractions.Loadouts.Sorting;

/// <summary />
[JsonName("SilverModManager.Abstractions.DataModel.Entities.Sorting.After")]
public record After<TType, TId> : ISortRule<TType, TId>
{
    /// <summary>
    ///     ID of the other mod this mod should be placed after.
    /// </summary>
    public required TId Other { get; init; }
}
