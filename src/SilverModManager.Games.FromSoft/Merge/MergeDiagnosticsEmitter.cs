using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
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
public class MergeDiagnosticsEmitter(MergePlanCache plans, ILogger<MergeDiagnosticsEmitter> logger) : IFromSoftDiagnosticEmitter
{
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        MergePlan plan;
        MergeRefusedException? failure = null;
        try
        {
            plan = plans.Current(loadout, MergeSourceMode.Report);
        }
        catch (MergeRefusedException refused)
        {
            failure = refused;
            plan = new MergePlan([], null, []);
        }
        catch (Exception exception) when (MergeFailures.IsInputProblem(exception))
        {
            logger.LogWarning(exception, "Planning the merges of {Loadout} failed", loadout.Name);
            failure = MergeRefusedException.PlanFailed(exception);
            plan = new MergePlan([], null, []);
        }

        if (failure is not null)
        {
            yield return FromSoftDiagnostics.CreateMergeRefused(failure.Message, "", "");
            yield break;
        }

        if (plan.Refusal is { } refusal)
        {
            var message = new MergeRefusedException(refusal).Message;
            if (refusal.Missing.Count > 0)
            {
                // The choices are on the diagnostic rather than in the message, so the apply's dialog and this text stay one sentence.
                yield return FromSoftDiagnostics.CreateVanillaMissing(message, refusal.Path) with { Actions = [..VanillaMissingActions.For(refusal.Missing)] };
                yield break;
            }

            yield return FromSoftDiagnostics.CreateMergeRefused(message, refusal.Path, MergeStrategies.Name(refusal.Strategy));
            yield break;
        }

        foreach (var warning in PackageCollisionEmitter.WinnerWarnings(plan.Unmerged)) yield return warning;

        foreach (var merged in plan.Merged)
        {
            var mods = string.Join(", ", merged.Contributors.Select(c => c.ModName).Distinct(StringComparer.Ordinal));
            foreach (var note in merged.Notes)
            {
                // Which original copy a merge compared against is for the merge report; the user has nothing to decide about it.
                if (note is IVanillaSourceNote or VanillaNearestChosen or VanillaAlternateChosen) continue;
                yield return FromSoftDiagnostics.CreateMergeNote(MergeSubject(merged.Path), merged.Path, mods, note.Summary(), Advice(note));
            }
        }
    }

    // The talk archives are named by map, which says more to a player than the file extension does.
    private static string MergeSubject(string path)
    {
        const string talk = ".talkesdbnd.dcx";
        return path.EndsWith(talk, StringComparison.OrdinalIgnoreCase)
            ? $"the talk script {System.IO.Path.GetFileName(path)[..^talk.Length]}"
            : path;
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
