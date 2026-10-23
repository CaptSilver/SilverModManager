using NexusMods.Abstractions.Games;
using NexusMods.App.UI.Resources;

namespace NexusMods.App.UI.Pages.Sorting;

/// <summary>
/// Words the result of a LOOT sort for the panel under the Load Order page's toolbar.
/// </summary>
public static class LootOutcomeText
{
    /// <summary>
    /// The masterlist revision, then one line for each rule set the sort could not apply. A failed sort leads with
    /// the cycle instead, since that is what the user has to act on.
    /// </summary>
    public static string Describe(LootSortOutcome outcome)
    {
        var lines = new List<string>();
        if (outcome.CycleText is not null) lines.Add(outcome.CycleText);
        lines.Add(string.Format(Language.LoadOrder_SortWithLoot_Revision, outcome.MasterlistRevision));
        if (outcome.OverlapEdgesSkipped) lines.Add(Language.LoadOrder_SortWithLoot_OverlapSkipped);
        if (outcome.UnevaluatedConditions > 0) lines.Add(string.Format(Language.LoadOrder_SortWithLoot_UnevaluatedConditions, outcome.UnevaluatedConditions));
        return string.Join(Environment.NewLine, lines);
    }
}
