using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Windows;
using SilverModManager.DataModel;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Removes the backups of a game's own files that no loadout needs, after saying how much that frees and asking.
/// What the dialog names is measured again when the button is pressed, so it is never a stale figure, and a backup a
/// loadout needs is never in it.
/// </summary>
[UsedImplicitly]
public sealed class CleanGameFileBackupsActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, GameBackupSpace space, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.CleanGameFileBackupsId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) return;

        var install = loadout.InstallationId;
        var report = await space.Measure(install);
        if (report.RemovableCount == 0) return;

        if (!await Confirm(report)) return;

        await space.RemoveUnneeded(install);
        await diagnosticManager.RefreshAsync(loadoutId);
    }

    private async Task<bool> Confirm(BackupSpaceReport report)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            title: "Clean up backups?",
            new StandardDialogParameters
            {
                Text = $"{BackupOverflow.Files(report.RemovableCount)} ({BackupOverflow.FormatSize(report.Removable)}) of the game's original files are not needed by any loadout. " +
                       "They will be deleted and cannot be brought back. Backups a loadout still needs are kept.",
            },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Delete", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ]
        );

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }
}
