using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Overlay;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Resolves one Health Check finding about a modded session's overlay: after asking, deletes the file the finding
/// names from the overlay's upper folder (never from the game folder) and drops the finding. A finding about the
/// game folder has no file to delete; discarding it only says it has been looked at.
/// </summary>
[UsedImplicitly]
public sealed class DiscardOverlayFindingActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, UpperdirAuditEmitter emitter, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => UpperdirAuditEmitter.DiscardActionId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        if (arguments.Length != 3) throw new ArgumentException("Expected the loadout id, the finding's kind and its path.", nameof(arguments));

        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var installation = loadout.InstallationInstance;
        if (await GameRunningRefusal.RefuseIfRunning(serviceProvider, installation, "discarding the overlay finding")) return;
        if (!await Confirm(arguments[1], arguments[2])) return;

        // The dialog can sit open while the game starts.
        if (await GameRunningRefusal.RefuseIfRunning(serviceProvider, installation, "discarding the overlay finding")) return;

        await Task.Run(() => emitter.Discard(loadout, arguments));
        await diagnosticManager.RefreshAsync(loadoutId);
    }

    private async Task<bool> Confirm(string kind, string path)
    {
        var text = kind == nameof(AuditFindingKind.GameFolderChanged)
            ? $"'{path}' in the game folder will stop being reported. The game folder itself is not changed."
            : $"'{path}' will be deleted from the overlay's upper folder. The game folder is not touched.";

        var dialog = DialogFactory.CreateStandardDialog(
            title: "Discard this finding?",
            new StandardDialogParameters { Text = text },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Discard", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ]
        );

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }
}
