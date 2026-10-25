using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;

/// <summary>The two questions of the save step as dialogs.</summary>
public sealed class SaveToLibraryPrompts(IServiceProvider provider, IWindowManager windowManager) : ISavePrompts
{
    private const string Title = "Save these mods to your library first";

    public async Task<ImmutableArray<ModToSave>?> ChooseAsync(SavePlan plan, CancellationToken cancellationToken)
    {
        var viewModel = Describe(plan);
        var accept = plan.Save.IsEmpty ? "Continue" : "Save and continue";
        var dialog = DialogFactory.CreateDialog(
            Title,
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(accept, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ],
            viewModel,
            DialogWindowSize.Large);

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept ? viewModel.Checked() : null;
    }

    public async Task<bool> ContinueAfterFailuresAsync(SaveReport report, CancellationToken cancellationToken)
    {
        var text = new StringBuilder("These mods could not be added to your library:").AppendLine().AppendLine();
        foreach (var failed in report.Failed) text.AppendLine($"- {failed.Mod.Name} {failed.Mod.Version}: {failed.Message}");
        text.AppendLine().Append("Continuing deletes the old copies in the game folder anyway.");

        var dialog = DialogFactory.CreateStandardDialog(
            title: "Some mods were not saved",
            new StandardDialogParameters { Text = text.ToString() },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Continue anyway", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ],
            windowSize: DialogWindowSize.Large);

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }

    private SaveToLibraryDialogViewModel Describe(SavePlan plan)
    {
        var domains = provider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        var rows = plan.Save.Select(mod => new SaveToLibraryRow(mod, plan.Game is { } game
            ? NexusModsUrlBuilder.GetFileDownloadUri(domains.GetDomain(game), ModId.From(mod.ModId), FileId.From(mod.FileId), useNxmLink: false).ToString()
            : "")).ToArray();

        var notes = plan.Listed.Select(l => $"{l.Id}: {l.Detail}").Concat(plan.Problems).ToArray();
        var intro = plan.Save.IsEmpty
            ? "The cleanup deletes the mods another manager left in the game folder. None of them could be matched to a download, so nothing is saved first."
            : "The cleanup deletes the mods another manager left in the game folder. These can be downloaded from Nexus Mods again, and the ticked ones are added to your library before anything is deleted. "
              + "A free account gets each file's page opened instead; download it there and it lands in your library.";
        return new SaveToLibraryDialogViewModel(intro, rows, notes);
    }
}
