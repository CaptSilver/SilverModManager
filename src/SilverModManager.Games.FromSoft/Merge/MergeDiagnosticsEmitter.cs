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

        foreach (var fallback in plan.Unmerged)
            yield return PackageCollisionEmitter.WinnerWarning(fallback, fallback.Winner ?? throw new InvalidOperationException($"{fallback.Path} fell back to loadout priority but has no winner"));

        foreach (var merged in plan.Merged)
        {
            var mods = string.Join(", ", merged.Contributors.Select(c => c.ModName).Distinct(StringComparer.Ordinal));
            foreach (var note in merged.Notes)
            {
                // What an apply would keep and where the ancestor came from are information; the user has nothing to decide about them.
                if (note is IVanillaSourceNote) continue;
                yield return FromSoftDiagnostics.CreateMergeNote(merged.Path, mods, note.Describe());
            }
        }
    }
}
