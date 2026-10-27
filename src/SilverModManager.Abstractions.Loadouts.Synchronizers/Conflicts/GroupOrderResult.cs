namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <param name="Rule">A stored rule the final order honours over the requested one.</param>
/// <param name="RequestedWinner">The group the caller put above <paramref name="RequestedLoser"/>; the rule reverses them.</param>
/// <param name="Displaced">True when no chain of rules forces the reversal: the pair only moved because the stored rule had to be repaired elsewhere in the order.</param>
public sealed record OverriddenOrder(LoadoutGroupRule.ReadOnly Rule, LoadoutItemGroupId RequestedWinner, LoadoutItemGroupId RequestedLoser, bool Displaced = false);

/// <summary>What setting a group order did beyond writing it.</summary>
/// <param name="Overridden">Pairs of the requested order that a stored rule turned round.</param>
/// <param name="Resolved">The requested groups in the order the priority rows end up with, winner first: the request with the stored rules applied.</param>
public sealed record GroupOrderResult(IReadOnlyList<OverriddenOrder> Overridden, IReadOnlyList<LoadoutItemGroupId> Resolved)
{
    public static readonly GroupOrderResult None = new([], []);
}
