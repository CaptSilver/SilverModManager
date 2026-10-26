using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Builds a <see cref="MergeReport"/> from the merge index and the cached plan in report mode, which are the two
/// things the apply itself reads. The report therefore says what the apply would do, and since report mode writes
/// nothing, asking for it changes nothing.
/// </summary>
public sealed class MergeReports(
    IConnection connection, LoadoutMergeSources sources, MergePlanCache plans, VanillaDumps dumps, ILogger<MergeReports> logger) : IMergeReports
{
    private const string HeldBack = "not merged: the apply is refused";

    public Task<MergeReport> ForLoadout(LoadoutId id, CancellationToken token)
        => Task.Run(() => Build(Loadout.Load(connection.Db, id)), token);

    private MergeReport Build(Loadout.ReadOnly loadout)
    {
        MergePathIndex index;
        MergePlan? plan = null;
        string? refusal = null;
        try
        {
            index = sources.Index(loadout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Without an index there are no paths to list, only the reason the apply would stop.
            return new MergeReport([], Failed(loadout, exception), MergeReport.DefaultPriorityOrderHint);
        }

        try
        {
            plan = plans.Current(loadout, MergeSourceMode.Report);
            if (plan.Refusal is { } refused) refusal = new MergeRefusedException(refused).Message;
        }
        catch (MergeRefusedException refused)
        {
            refusal = refused.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            refusal = Failed(loadout, exception);
        }

        var missingVanilla = index.Candidates.Any(c => c.Outcome.Reason == MergeOutcome.NoVanillaSource);
        var unconfirmed = missingVanilla ? dumps.Unconfirmed(loadout.Db, loadout.InstallationInstance) : [];
        var archiveFailure = missingVanilla ? sources.GameArchiveFailure(loadout.InstallationInstance) : null;

        var entries = index.Candidates
            .Select(c => EntryOf(c, plan, refusal, unconfirmed, archiveFailure))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToArray();
        return new MergeReport(entries, refusal, MergeReport.DefaultPriorityOrderHint);
    }

    // Same words Health Check and the apply use for a plan that could not be made at all.
    private string Failed(Loadout.ReadOnly loadout, Exception exception)
    {
        logger.LogError(exception, "Planning the merges of {Loadout} failed", loadout.Name);
        return MergeRefusedException.PlanFailed(exception).Message;
    }

    private static MergeReportEntry EntryOf(MergeCandidate candidate, MergePlan? plan, string? refusal, IReadOnlyList<UnconfirmedDump> unconfirmed, string? archiveFailure)
    {
        var strategy = candidate.Strategy is { } s ? MergeStrategies.Name(s) : string.Empty;
        var mods = candidate.Contributors.Select(c => c.ModName).Distinct(StringComparer.Ordinal).ToArray();

        if (!candidate.Outcome.Merges)
        {
            // An unrecognised dump may be why a file that looks mergeable has no vanilla source, so it rides on the files it
            // holds a copy of. A game archive that could not be read may have held any of them.
            // A file with no strategy still has a winner, so the report says which copy me3 mounts.
            var notes = candidate.Outcome.Reason == MergeOutcome.NoVanillaSource
                ? unconfirmed.Where(d => d.HoldsCopyOf(candidate.Path)).Select(d => d.Describe(candidate.Path))
                    .Concat(archiveFailure is null ? [] : [archiveFailure]).ToArray()
                : candidate.Winner is { } winner ? [$"{winner.ModName} wins by loadout priority; the other copies are ignored"]
                : [];
            return new MergeReportEntry(candidate.Path, strategy, mods, MergeReportOutcome.NotMergeable, notes, string.Empty, candidate.Outcome.Reason ?? string.Empty);
        }

        if (plan?.Merged.FirstOrDefault(m => m.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)) is { } merged)
        {
            // Where the ancestor came from has its own field, so its notes are not repeated among the choices the merge made.
            // Mods of several builds each have a source of their own; with one build the first note says it all.
            var groups = merged.Notes.OfType<VanillaGroupSource>().Select(n => n.Describe()).ToArray();
            var source = groups.Length > 0
                ? string.Join("; ", groups)
                : merged.Notes.OfType<BaselineWillBeKept>().Select(n => n.Describe())
                    .Concat(merged.Notes.OfType<VanillaServedFromDump>().Select(n => n.Describe()))
                    .Concat(merged.Notes.OfType<VanillaServedFromGameArchive>().Select(n => n.Describe()))
                    .FirstOrDefault() ?? merged.VanillaSource;
            var notes = merged.Notes.Where(n => n is not IVanillaSourceNote).Select(n => n.Describe()).ToArray();
            return new MergeReportEntry(candidate.Path, strategy, mods, MergeReportOutcome.Merged, notes, source, string.Empty);
        }

        var reason = plan?.Refusal is { } r && r.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase) && refusal is { } text
            ? text
            : HeldBack;
        return new MergeReportEntry(candidate.Path, strategy, mods, MergeReportOutcome.Refused, [], string.Empty, reason);
    }
}
