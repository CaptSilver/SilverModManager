using NexusMods.Abstractions.Serialization.Attributes;

namespace NexusMods.Abstractions.Loadouts.Sorting;

/// <summary />
[JsonName("SilverModManager.Abstractions.DataModel.Entities.Sorting.First")]
public record First<TType, TId> : ISortRule<TType, TId>;
