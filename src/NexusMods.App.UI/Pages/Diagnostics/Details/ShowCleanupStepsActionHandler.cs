using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Windows;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Dialog;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.App.UI.Pages.Diagnostics;

/// <summary>
/// Shows the steps of a game's guided cleanup as a list. It runs nothing: the steps change the game folder, so they
/// are run from the command line after reading them.
/// </summary>
[UsedImplicitly]
public sealed class ShowCleanupStepsActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => GameCleanupActions.ShowStepsId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var guide = serviceProvider.GetServices<IGameCleanupGuide>().FirstOrDefault(g => g.AppliesTo(loadout.InstallationInstance))
                    ?? throw new InvalidOperationException($"{loadout.InstallationInstance.Game.DisplayName} has no cleanup steps to show.");

        // Planning reads the game folder and Steam's files, so it stays off the UI thread.
        var lines = await Task.Run(() => guide.Describe(loadout));

        var dialog = DialogFactory.CreateStandardDialog(
            title: "Cleanup steps",
            new StandardDialogParameters { Text = string.Join('\n', lines) },
            buttonDefinitions: [DialogStandardButtons.Ok],
            windowSize: DialogWindowSize.Large);

        await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
    }
}
