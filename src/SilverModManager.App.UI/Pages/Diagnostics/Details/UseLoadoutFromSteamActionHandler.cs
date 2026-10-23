using System.Collections.Immutable;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.App.UI.Pages.SteamLaunchOption;
using SilverModManager.Sdk.Loadouts;
using Microsoft.Extensions.DependencyInjection;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics;

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
