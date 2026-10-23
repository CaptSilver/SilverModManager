using JetBrains.Annotations;

namespace SilverModManager.Abstractions.Loadouts.Sorting;

[PublicAPI]
public record Last<TType, TId> : ISortRule<TType, TId>;
