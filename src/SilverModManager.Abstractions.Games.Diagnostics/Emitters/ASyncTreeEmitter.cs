using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// An emitter that compares what the loadout provides with what is in the game folder, which is what the sync tree
/// holds. Both <c>Diagnose</c> overloads are declared here because the interface's sync-tree overload defaults to the
/// tree-less one, and an emitter that overrides only one sends callers round in a circle.
/// </summary>
public abstract class ASyncTreeEmitter : ILoadoutDiagnosticEmitter
{
    /// <summary>
    /// The diagnostics for the loadout, given the tree the diagnostic manager built for it.
    /// </summary>
    protected abstract IAsyncEnumerable<Diagnostic> DiagnoseTree(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The manager always passes its tree; a caller that has none gets one built the way the apply builds it.
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        var tree = (await synchronizer.BuildSyncTree(loadout)).ToFrozenDictionary();
        await foreach (var diagnostic in DiagnoseTree(loadout, tree, cancellationToken).WithCancellation(cancellationToken))
            yield return diagnostic;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
        => DiagnoseTree(loadout, syncTree, cancellationToken);
}
