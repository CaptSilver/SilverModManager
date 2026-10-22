using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>What the apply would do with one path that more than one mod provides.</summary>
public enum MergeReportOutcome
{
    /// <summary>The apply writes a merged file for the path.</summary>
    Merged,

    /// <summary>The path could not be merged, or another path's failure stops the whole apply before this one is written.</summary>
    Refused,

    /// <summary>The engine has no merge for the path, so the contributors are deployed as they are.</summary>
    NotMergeable,
}

/// <summary>The one wording of a <see cref="MergeReportOutcome"/> that the page and the command line both print.</summary>
public static class MergeReportOutcomes
{
    public static string Label(this MergeReportOutcome outcome) => outcome switch
    {
        MergeReportOutcome.Merged => "merged",
        MergeReportOutcome.Refused => "refused",
        MergeReportOutcome.NotMergeable => "not merged",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown merge outcome"),
    };
}

/// <summary>One path of a <see cref="MergeReport"/>.</summary>
/// <param name="Path">The package-relative path.</param>
/// <param name="Strategy">The strategy's name as <see cref="MergeStrategies.Name"/> spells it; empty when the engine has none.</param>
/// <param name="Contributors">The mods that provide the path, the highest priority first, which is the order they are folded in.</param>
/// <param name="Outcome">What the apply does with the path.</param>
/// <param name="Notes">What the merge kept, dropped or took whole, as sentences.</param>
/// <param name="VanillaSource">Where the ancestor of the merge comes from; empty for the unions, which compare nothing.</param>
/// <param name="Reason">Why the path is refused or not mergeable; empty when it is merged.</param>
public sealed record MergeReportEntry(
    string Path,
    string Strategy,
    IReadOnlyList<string> Contributors,
    MergeReportOutcome Outcome,
    IReadOnlyList<string> Notes,
    string VanillaSource,
    string Reason);

/// <summary>
/// Everything the merges of a loadout would do, as plain records, so the page and the command line print the same
/// facts.
/// </summary>
/// <param name="Entries">Every path more than one mod provides, sorted by path.</param>
/// <param name="Refusal">The message the apply would stop with; null when it goes ahead.</param>
/// <param name="PriorityOrderHint">The sentence that tells the user how the contributor order is changed.</param>
public sealed record MergeReport(IReadOnlyList<MergeReportEntry> Entries, string? Refusal, string PriorityOrderHint)
{
    public const string DefaultPriorityOrderHint =
        "Contributors are ordered by the loadout's mod priority; change it under Rules, File Conflicts on the loadout page (Move up / Move down), which allows edits in a loadout with a single collection";
}

/// <summary>Reads the merge report of a loadout without writing anything.</summary>
public interface IMergeReports
{
    Task<MergeReport> ForLoadout(LoadoutId id, CancellationToken token);
}
