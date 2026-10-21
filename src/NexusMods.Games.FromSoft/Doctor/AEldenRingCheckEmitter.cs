using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// An emitter that shows results of <see cref="EldenRingChecks"/>. The checks read the disk, not the sync tree, so
/// they answer the same with or without one.
/// </summary>
public abstract class AEldenRingCheckEmitter : IFromSoftDiagnosticEmitter
{
    protected abstract IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout);

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var diagnostic in Diagnose(loadout)) yield return diagnostic;
    }
}
