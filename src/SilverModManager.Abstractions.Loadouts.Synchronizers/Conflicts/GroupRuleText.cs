using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// The one wording of a stored rule shared by cards, drop reasons and refusals: plain group names, no quotes, no markdown.
/// </summary>
public static class GroupRuleText
{
    /// <summary>"{winner} wins over {loser}".</summary>
    public static string WinsOver(LoadoutGroupRule.ReadOnly rule)
    {
        var source = rule.Source.AsLoadoutItem().Name;
        var other = rule.Other.AsLoadoutItem().Name;
        return rule.Kind == GroupRuleKind.After
            ? $"{source} wins over {other}"
            : $"{other} wins over {source}";
    }
}
