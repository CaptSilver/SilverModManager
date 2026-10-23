using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>
/// An emitter that shows results of <see cref="FromSoftChecks"/>. The checks read the disk, not the sync tree, so
/// they answer the same with or without one.
/// </summary>
public abstract class AFromSoftCheckEmitter : IFromSoftDiagnosticEmitter
{
    /// <summary>The diagnostics for a loadout of <paramref name="game"/>; only called for a FromSoft game.</summary>
    protected abstract IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, IFromSoftGame game);

    private IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout)
        => loadout.InstallationInstance.Game is IFromSoftGame game ? Diagnose(loadout, game) : [];

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var diagnostic in Diagnose(loadout)) yield return diagnostic;
    }
}
