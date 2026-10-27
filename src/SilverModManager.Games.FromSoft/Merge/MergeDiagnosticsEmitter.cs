using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Shows what the next apply would do with the paths several mods provide: a warning for each choice a merge made,
/// and a critical for a merge that can't be made. It reads the same plan the apply's preflight reads, so Health Check
/// says before the apply what the apply would refuse, and it writes nothing: <see cref="MergeSourceMode.Report"/> only
/// reads. Planning merges the files, which is the cost of the check; the apply that follows finds the plan cached.
/// </summary>
public class MergeDiagnosticsEmitter(MergePlanCache plans, ILogger<MergeDiagnosticsEmitter> logger)
    : AMergeDiagnosticsEmitter<MergePlan>(logger), IFromSoftDiagnosticEmitter
{
    protected override MergePlan? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure)
    {
        failure = null;
        try
        {
            var plan = plans.Current(loadout, MergeSourceMode.Report);
            if (plan.Refusal is not { } refusal) return plan;

            var message = new MergeRefusedException(refusal).Message;
            // The choices are on the diagnostic rather than in the message, so the apply's dialog and this text stay one sentence.
            failure = refusal.Missing.Count > 0
                ? FromSoftDiagnostics.CreateVanillaMissing(message, refusal.Path) with { Actions = [..VanillaMissingActions.For(refusal.Missing)] }
                : FromSoftDiagnostics.CreateMergeRefused(message, refusal.Path, MergeStrategies.Name(refusal.Strategy));
            return null;
        }
        catch (MergeRefusedException refused)
        {
            failure = FromSoftDiagnostics.CreateMergeRefused(refused.Message, "", "");
            return null;
        }
        catch (Exception exception) when (MergeFailures.IsInputProblem(exception))
        {
            Logger.LogWarning(exception, "Planning the merges of {Loadout} failed", loadout.Name);
            failure = FromSoftDiagnostics.CreateMergeRefused(MergeRefusedException.PlanFailed(exception).Message, "", "");
            return null;
        }
    }

    protected override IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, MergePlan plan)
        => plan.Merged.Select(merged => new MergedEntry(
            merged.Path,
            string.Join(", ", merged.Contributors.Select(c => c.ModName).Distinct(StringComparer.Ordinal)),
            merged.Notes));

    protected override IEnumerable<Diagnostic> Extra(Loadout.ReadOnly loadout, MergePlan plan)
        => PackageCollisionEmitter.WinnerWarnings(plan.Unmerged);

    // The cards say "Rules > File Conflicts", so the dropped items point at the same page.
    protected override string ReorderPage(Loadout.ReadOnly loadout) => "File Conflicts";

    protected override bool RaisesCard(IMergeNote note) => RaisesNoteCard(note);

    // The cards are FromSoft's own template, which also covers a note that records no loss.
    protected override IAsyncEnumerable<Diagnostic> NoteCards(Loadout.ReadOnly loadout, MergePlan plan, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
        => Merged(loadout, plan).SelectMany(entry => NoteCards(entry.Path, entry.Mods, entry.Notes)).ToAsyncEnumerable();

    // Which original copy a merge compared against, and what it took whole from one mod, are for the merge report;
    // the user has nothing to decide about either.
    private static bool RaisesNoteCard(IMergeNote note) => note is not (IVanillaSourceNote or IMergeDetailNote or VanillaNearestChosen or VanillaAlternateChosen);

    /// <summary>The card for each note that records a choice; notes about the source or what was taken without a choice raise none.</summary>
    internal static IEnumerable<Diagnostic> NoteCards(string path, string mods, IEnumerable<IMergeNote> notes)
    {
        foreach (var note in notes.Where(RaisesNoteCard))
            yield return FromSoftDiagnostics.CreateMergeNote(MergeSubject(path), path, mods, note.Summary(), Advice(note));
    }

    // The talk archives are named by map and the equipment parts by item, which say more to a player than the file extension does.
    private static string MergeSubject(string path)
    {
        const string talk = ".talkesdbnd.dcx";
        const string part = ".partsbnd.dcx";
        var name = System.IO.Path.GetFileName(path);
        if (path.EndsWith(talk, StringComparison.OrdinalIgnoreCase)) return $"the talk script {name[..^talk.Length]}";
        if (path.EndsWith(part, StringComparison.OrdinalIgnoreCase)) return $"the equipment part {name[..^part.Length]}";
        return path;
    }

    private static string Advice(IMergeNote note)
    {
        var advice = note.Loss is { } loss
            ? $"{loss.DroppedMod}'s change is not used. To use it instead, move {loss.DroppedMod} below {loss.KeptMod} on Rules > File Conflicts " +
              $"(in the default order, the mod lower in the list wins), or disable {loss.KeptMod}."
            : "Nothing is refused and the apply goes ahead.";
        return note.Details is { } details ? $"{advice}\n\nDetails: {details}." : advice;
    }
}
