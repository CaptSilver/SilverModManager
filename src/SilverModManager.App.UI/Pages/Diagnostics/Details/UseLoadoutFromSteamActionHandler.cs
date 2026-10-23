using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.App.UI.Pages.SteamLaunchOption;
using NexusMods.Sdk.Loadouts;
using Microsoft.Extensions.DependencyInjection;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.App.UI.Pages.Diagnostics;

/// <summary>
/// Opens "Use this loadout from Steam" for the diagnostic's loadout.
/// </summary>
[UsedImplicitly]
public sealed class UseLoadoutFromSteamActionHandler(IServiceProvider serviceProvider, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.UseLoadoutFromSteamId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        await SteamLaunchOptionDialog.ShowAsync(serviceProvider, loadout);

        // Closing the dialog fires no focus or window event, and the launch options are not in the database,
        // so nothing else would rerun the emitters.
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
