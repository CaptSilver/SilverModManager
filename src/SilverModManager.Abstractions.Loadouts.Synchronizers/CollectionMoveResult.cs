using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>What a move into a collection did: the mod groups that moved and the items the plan left where they were.</summary>
public sealed record CollectionMoveResult(IReadOnlyList<LoadoutItemGroupId> Moved, IReadOnlyList<CollectionMoveSkip> Skipped);
