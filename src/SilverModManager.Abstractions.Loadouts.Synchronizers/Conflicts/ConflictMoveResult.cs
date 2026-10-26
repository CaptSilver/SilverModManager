namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// The outcome of moving groups in the priority order: done, or refused because the new order would break a
/// group rule. A refused move writes nothing.
/// </summary>
public readonly struct ConflictMoveResult
{
    private readonly LoadoutGroupRule.ReadOnly _rule;

    private ConflictMoveResult(LoadoutGroupRule.ReadOnly rule, bool isRuleViolation)
    {
        _rule = rule;
        IsRuleViolation = isRuleViolation;
    }

    /// <summary>True when the move was refused because it would break <see cref="Rule"/>.</summary>
    public bool IsRuleViolation { get; }

    /// <summary>The rule the move would have broken. Throws unless <see cref="IsRuleViolation"/>.</summary>
    public LoadoutGroupRule.ReadOnly Rule => IsRuleViolation ? _rule : throw new InvalidOperationException("The move was done: no rule stops it.");

    /// <summary>The move was written.</summary>
    public static ConflictMoveResult Moved => default;

    /// <summary>The move was refused because it would break <paramref name="rule"/>.</summary>
    public static ConflictMoveResult RuleViolation(LoadoutGroupRule.ReadOnly rule) => new(rule, isRuleViolation: true);
}
