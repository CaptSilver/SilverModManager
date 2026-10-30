using Microsoft.Extensions.DependencyInjection;
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
/// Puts the removal question to the player in a modal dialog. Closing the window answers Cancel: a closed window is
/// not consent to delete a mod setup.
/// </summary>
public sealed class DialogMassRemovalPrompt(IServiceProvider serviceProvider, IWindowManager windowManager) : IMassRemovalPrompt
{
    /// <inheritdoc />
    public async ValueTask<bool> Confirm(GameInstallation installation, MassRemoval removal, CancellationToken cancellationToken)
    {
        // Close only works once the window exists, so a sync cancelled before this point must not open the dialog.
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = MassRemovalPrompt.For(installation.Game.DisplayName, removal);

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
        return pressed == ButtonDefinitionId.Accept;
    }
}
