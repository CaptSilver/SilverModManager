using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.App.UI.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// Puts the backup-cap question to the player in a modal dialog. Closing the window answers the cancel button: a
/// closed window is not consent to copy gigabytes (after play it keeps the files, since nothing can be undone there).
/// </summary>
public sealed class DialogBackupOverflowPrompt(IServiceProvider serviceProvider, IWindowManager windowManager) : IBackupOverflowPrompt
{
    /// <inheritdoc />
    public async ValueTask<BackupOverflowChoice> Ask(GameInstallation installation, BackupOverflow overflow, CancellationToken cancellationToken)
    {
        // Close only works once the window exists, so a sync cancelled before this point must not open the dialog.
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = BackupOverflowPrompt.For(installation.Game.DisplayName, overflow);

        var pressed = await DispatcherHelper.EnsureOnUIThreadAsync(async () =>
        {
            var markdownVm = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
            markdownVm.Contents = prompt.Markdown;

            var dialog = DialogFactory.CreateStandardDialog(
                prompt.Title,
                new StandardDialogParameters { Markdown = markdownVm },
                prompt.Buttons
            );

            // The sync that asked was cancelled, so the question is moot; closing leaves the result at "none".
            using var _ = cancellationToken.Register(dialog.Close);
            return (await windowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId;
        });

        cancellationToken.ThrowIfCancellationRequested();

        if (pressed == ButtonDefinitionId.Accept) return BackupOverflowChoice.BackUpAnyway;
        if (pressed == BackupOverflowPrompt.Adopt) return BackupOverflowChoice.AdoptInPlace;

        // After the game closed its files are already on disk: closing the window can only mean "leave them", the
        // harmless answer, not a refusal that would report the loadout as still applied.
        if (overflow.Operation == BackupOverflowOperation.AfterPlay && overflow.CanAdopt) return BackupOverflowChoice.AdoptInPlace;
        return BackupOverflowChoice.Refuse;
    }
}
