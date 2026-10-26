using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Runs the prefix setup as a dry run and opens its preview, where the user confirms the prefix and starts the
/// real run. The components come from the diagnostic's action arguments.
/// </summary>
[UsedImplicitly]
public sealed class SetUpPrefixActionHandler(IServiceProvider serviceProvider, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.SetUpPrefixId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        if (arguments.IsDefaultOrEmpty) throw new ArgumentException("Expected at least one component id.", nameof(arguments));

        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var installation = loadout.InstallationInstance;
        var components = arguments.Select(id => new PrefixComponentId(id)).ToList();
        var runner = serviceProvider.GetRequiredService<IPrefixSetupRunner>();

        // A refusal here (no prefix yet, nothing can run in it) reaches the error presenter as a plain dialog.
        var dryRun = await Task.Run(() => runner.Run(installation, new PrefixSetupRequest { Components = components }, CancellationToken.None));

        var dialog = new PrefixSetupViewModel(runner, installation, dryRun.Plan, components);
        await serviceProvider.GetRequiredService<IOverlayController>().EnqueueAndWait(dialog);

        // The prefix is not in the database, so nothing else would rerun the emitter that raised the diagnostic.
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
