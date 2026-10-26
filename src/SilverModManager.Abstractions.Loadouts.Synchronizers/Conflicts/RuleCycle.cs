using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// Two groups whose ordering rules contradict each other, directly or through a chain of other groups.
/// </summary>
public readonly record struct RuleCycle(LoadoutItemGroupId First, LoadoutItemGroupId Second);

/// <summary>
/// The outcome of adding a group rule: the new rule's id, or the cycle that made it impossible.
/// Nothing is written when the rule would cycle.
/// </summary>
public readonly struct AddGroupRuleResult
{
    private readonly LoadoutGroupRuleId _ruleId;
    private readonly RuleCycle _cycle;

    private AddGroupRuleResult(LoadoutGroupRuleId ruleId, RuleCycle cycle, bool isCycle)
    {
        _ruleId = ruleId;
        _cycle = cycle;
        IsCycle = isCycle;
    }

    /// <summary>True when the rule was refused because it would create a cycle.</summary>
    public bool IsCycle { get; }

    /// <summary>The id of the written rule. Throws when <see cref="IsCycle"/>.</summary>
    public LoadoutGroupRuleId RuleId => IsCycle ? throw new InvalidOperationException("The rule was refused: it would create a cycle.") : _ruleId;

    /// <summary>The conflicting groups. Throws unless <see cref="IsCycle"/>.</summary>
    public RuleCycle Cycle => IsCycle ? _cycle : throw new InvalidOperationException("The rule was added: there is no cycle.");

    /// <summary/>
    public static AddGroupRuleResult Added(LoadoutGroupRuleId ruleId) => new(ruleId, default, isCycle: false);

    /// <summary/>
    public static AddGroupRuleResult Cyclic(RuleCycle cycle) => new(default, cycle, isCycle: true);
}
