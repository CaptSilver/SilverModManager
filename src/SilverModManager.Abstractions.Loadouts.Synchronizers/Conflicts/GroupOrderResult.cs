namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <param name="Rule">A stored rule the final order honours over the requested one.</param>
/// <param name="RequestedWinner">The group the caller put above <paramref name="RequestedLoser"/>; the rule reverses them.</param>
public sealed record OverriddenOrder(LoadoutGroupRule.ReadOnly Rule, LoadoutItemGroupId RequestedWinner, LoadoutItemGroupId RequestedLoser);

/// <summary>What setting a group order did beyond writing it.</summary>
/// <param name="Overridden">Pairs of the requested order that a stored rule turned round.</param>
public sealed record GroupOrderResult(IReadOnlyList<OverriddenOrder> Overridden)
{
    public static readonly GroupOrderResult None = new([]);
}
