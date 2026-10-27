using System.Collections.Immutable;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Hides the note about a game that moved folders. The note is shown while the installation records where it
/// came from, so dismissing it forgets that. It is the installation's note: the loadouts on the same install
/// stop showing it too.
/// </summary>
[UsedImplicitly]
public sealed class DismissGameMovedActionHandler(IConnection connection, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.DismissGameMovedId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var loadout = Loadout.Load(connection.Db, loadoutId);
        if (loadout.IsValid() && GameInstallMetadata.MovedFrom.Contains(loadout.Installation))
        {
            using var tx = connection.BeginTransaction();
            tx.RetractCurrent(loadout.Installation.Id, GameInstallMetadata.MovedFrom);
            await tx.Commit();
        }

        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
