using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Build;

/// <summary>
/// Warns when the installed build is not the one stamped at the first launch, naming each field that moved. An
/// installation that was never launched has no stamp and nothing to compare with, so it has no diagnostic.
/// </summary>
public class BuildDriftEmitter(BuildStampStore stamps) : IFromSoftDiagnosticEmitter
{
    /// <summary>The id of the action that stamps the installed build. Its one argument is the loadout id.</summary>
    public const string RestampActionId = "restamp-build";

    public static DiagnosticAction RestampAction(LoadoutId loadoutId) => new(RestampActionId, "Re-stamp", [loadoutId.Value.Value.ToString()]);

    // Compares the live build with the stamp, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var stamped = stamps.Find(loadout.InstallationId);
        if (stamped is null) yield break;

        var live = BuildIdentity.Identify(loadout.InstallationInstance);
        var kind = BuildIdentity.Classify(stamped, live);
        if (kind == BuildChangeKind.Unchanged) yield break;

        var moved = string.Join("; ", BuildIdentity.Drift(stamped, live).Select(c => $"{c.Field} {c.Stamped} -> {c.Live}"));
        yield return FromSoftDiagnostics.CreateBuildChanged(kind.ToString().ToLowerInvariant(), moved) with { Actions = [RestampAction(loadout.LoadoutId)] };
    }
}
