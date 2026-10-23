using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Moves a backup folder back in place of a dangling link, after the user confirms. The two paths come from
/// the diagnostic's action arguments (link, then backup).
/// </summary>
[UsedImplicitly]
public sealed class RestoreFolderFromBackupActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.RestoreFolderFromBackupId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        if (arguments.Length != 2) throw new ArgumentException("Expected the link path and the backup path.", nameof(arguments));
        var (linkPath, backupPath) = (arguments[0], arguments[1]);

        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var installation = loadout.InstallationInstance;
        if (await RefuseIfRunning(installation)) return;

        if (!await Confirm(linkPath, backupPath)) return;

        // Everything is checked again after the answer, the folders inside Restore as the last thing before the
        // file system changes: the dialog can sit open while the game starts or another tool touches the folders.
        if (await RefuseIfRunning(installation)) return;

        try
        {
            SavedFolderRestore.Restore(linkPath, backupPath);
        }
        catch (InvalidOperationException e)
        {
            await MessageBoxOkViewModel.Show(serviceProvider, "Restore did not finish", e.Message);
            await diagnosticManager.RefreshAsync(loadoutId);
            return;
        }

        await diagnosticManager.RefreshAsync(loadoutId);
    }

    private async Task<bool> RefuseIfRunning(GameInstallation installation)
    {
        if (!serviceProvider.GetRequiredService<IGameLaunchSession>().IsRunning(installation)) return false;

        await MessageBoxOkViewModel.ShowGameAlreadyRunningError(serviceProvider, installation.Game.DisplayName);
        return true;
    }

    private async Task<bool> Confirm(string linkPath, string backupPath)
    {
        var dialog = DialogFactory.CreateStandardDialog(
            title: "Restore backup folder?",
            new StandardDialogParameters
            {
                Text = $"The folder '{linkPath}' is a link to a folder that no longer exists. It will be removed, and the backup " +
                       $"'{backupPath}' will be moved to '{linkPath}'. No files are deleted or copied.",
            },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Restore", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ]
        );

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }
}
