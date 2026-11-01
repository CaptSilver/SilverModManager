using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Puts the game's own regulation back over a modified one in the real game folder, then says where the replaced file
/// was kept and brings the build stamp's regulation fields to the restored file. The diagnostic's note already says what the button does, so there is no extra question. A refusal is
/// thrown for the details page to show.
/// </summary>
[UsedImplicitly]
public sealed class RestoreRegulationActionHandler(IServiceProvider serviceProvider, RegulationRestore restore, BuildStampStore stamps, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => RegulationRestore.RestoreActionId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var restored = await Task.Run(() => restore.Restore(loadout.InstallationInstance, stamps.Find(loadout.InstallationId)));

        // The stamp may describe the modded file that was just replaced, which would now read as tampered.
        await stamps.AdoptRegulation(loadout.InstallationId, BuildIdentity.Identify(loadout.InstallationInstance));
        await diagnosticManager.RefreshAsync(loadoutId);

        await MessageBoxOkViewModel.Show(serviceProvider, "Regulation restored", Describe(restored));
    }

    private static string Describe(RegulationRestored restored)
        => restored switch
        {
            { Changed: false } => "The regulation already is the game's own file, so nothing was changed.",
            { SetAside: { } kept } => $"The game's own regulation is back in the game folder. The file that was there is kept at {kept}.",
            _ => "The game's own regulation is back in the game folder.",
        };
}
