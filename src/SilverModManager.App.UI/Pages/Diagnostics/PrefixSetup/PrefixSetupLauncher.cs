using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Overlays;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;

/// <inheritdoc />
[UsedImplicitly]
public sealed class PrefixSetupLauncher(IServiceProvider serviceProvider) : IPrefixSetupLauncher
{
    /// <inheritdoc />
    public async Task ShowAsync(LoadoutId loadoutId, IReadOnlyList<PrefixComponentId> components)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var installation = loadout.InstallationInstance;
        var runner = serviceProvider.GetRequiredService<IPrefixSetupRunner>();

        // A refusal here (no prefix yet, nothing can run in it) reaches the error presenter as a plain dialog.
        var dryRun = await Task.Run(() => runner.Run(installation, new PrefixSetupRequest { Components = components }, CancellationToken.None));

        var dialog = new PrefixSetupViewModel(runner, installation, dryRun.Plan, components);
        await serviceProvider.GetRequiredService<IOverlayController>().EnqueueAndWait(dialog);
    }
}
