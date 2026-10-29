using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Resources;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Imports a collection's load order again, for the card that lists the entries an earlier import left out. The work runs
/// off the UI thread; a refusal (the collection is installing or updating, or the game is running) reaches the user as the dialog the details page shows.
/// </summary>
[UsedImplicitly]
public sealed class ReapplyCollectionOrderActionHandler(IServiceProvider serviceProvider, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.ReapplyCollectionOrderId;

    /// <inheritdoc />
    public string? LabelFor(DiagnosticAction action) => Language.DiagnosticAction_ReapplyCollectionOrder;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        if (arguments.Length != 1 || !ulong.TryParse(arguments[0], out var id))
            throw new ArgumentException("The action names no collection to apply the load order of", nameof(arguments));

        var group = NexusCollectionLoadoutGroup.Load(serviceProvider.GetRequiredService<IConnection>().Db, EntityId.From(id));
        if (!group.IsValid()) throw new CollectionOrderReapplyRefusedException("The collection is no longer installed in this loadout.");

        await Task.Run(() => CollectionOrderReapply.Reapply(serviceProvider, group));
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
