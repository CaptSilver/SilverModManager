using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>One card shape for everything the app leaves out on purpose: what, why, and where to fix it.</summary>
public sealed record DroppedItem(string Item, string Source, string Reason, string Page);

/// <summary>
/// Turns what a loadout's next apply will not use into one Health Check warning per item.
/// Both <c>Diagnose</c> overloads are declared here because the interface's sync-tree overload defaults to the
/// tree-less one: an emitter that overrides only one would send callers round in a circle.
/// </summary>
public abstract class ADroppedItemEmitter : ILoadoutDiagnosticEmitter
{
    /// <summary>
    /// The items the loadout's next apply will not use. Async over the sync tree because an emitter that computes a
    /// merge plan needs both; one that needs neither ignores them.
    /// </summary>
    protected abstract IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken);

    /// <summary>The Health Check card for one dropped item.</summary>
    public static Diagnostic Card(DroppedItem item) => DroppedItemDiagnostics.CreateDroppedItem(item.Item, item.Source, item.Reason, item.Page);

    /// <inheritdoc />
    public virtual IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        return Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);
    }

    /// <inheritdoc />
    public virtual async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in Dropped(loadout, syncTree, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return Card(item);
        }
    }
}
