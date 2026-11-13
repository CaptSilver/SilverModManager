using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Moves the files a finding names out of the game folder into the loadout's disabled set-aside group, after asking.
/// A path the finding's group has no row for (a file dropped in after the last apply) is skipped and named: only an
/// apply can adopt it. Asks a question, so the command line refuses it.
/// </summary>
[UsedImplicitly]
public sealed class SetAsideActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.SetAsideLoadoutFilesId;

    /// <inheritdoc />
    public string? LabelFor(DiagnosticAction action) => Language.DiagnosticAction_SetAsideLoadoutFiles;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var (paths, group) = DiagnosticActions.ParseSetAsideLoadoutFiles(arguments);
        var activity = Language.SetAside_Activity;
        var errors = serviceProvider.GetRequiredService<IErrorPresenter>();
        var toasts = serviceProvider.GetRequiredService<IWindowNotificationService>();
        try
        {
            var withRows = paths.Distinct().Where(path => HasRowIn(loadoutId, group, path)).ToArray();
            var skipped = paths.Distinct().Except(withRows).ToArray();

            // With no row at all the set-aside service's own refusal tells the user why; with some, only those go.
            var request = withRows.Length == 0 ? paths.Distinct().ToArray() : withRows;
            var dialog = DialogFactory.CreateStandardDialog(
                Language.SetAside_Title,
                new StandardDialogParameters { Text = SetAsideText.For(request) },
                [
                    DialogStandardButtons.Cancel,
                    new DialogButtonDefinition(Language.SetAside_Confirm, ButtonDefinitionId.Accept, ButtonAction.Accept),
                ]);
            var answer = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (answer.ButtonId != ButtonDefinitionId.Accept) return;

            var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
            var setAside = serviceProvider.GetRequiredService<ISetAsideFiles>();
            // Builds the whole sync tree for the apply, so it stays off the UI thread.
            var result = await Task.Run(() => setAside.SetAside(loadout, request, CancellationToken.None));

            var count = result.Moved.Count == 1 ? "1 file" : $"{result.Moved.Count} files";
            if (result.Applied)
                toasts.ShowToast(string.Format(Language.SetAside_Toast, count));
            else
                toasts.ShowToast(string.Format(Language.SetAside_ApplyFailedToast, count), ToastNotificationVariant.Failure);

            if (skipped.Length > 0 && withRows.Length > 0)
                toasts.ShowToast(string.Format(Language.SetAside_SkippedToast, string.Join(", ", skipped.Select(p => MarkdownText.Code(p.Path.ToString())))));
        }
        catch (Exception e)
        {
            await errors.Report(e, activity);
            return;
        }

        await diagnosticManager.RefreshAsync(loadoutId);
    }

    private bool HasRowIn(LoadoutId loadoutId, LoadoutItemGroupId group, GamePath path)
        => LoadoutItemWithTargetPath.FindByTargetPath(serviceProvider.GetRequiredService<IConnection>().Db, path.ToGamePathParentTuple(loadoutId))
            .Any(row => row.AsLoadoutItem() is { } item && item.HasParent() && item.ParentId.Value == group.Value);
}

/// <summary>
/// The words of the dialog that asks the user to confirm setting files aside. Pure, so the wording can be tested
/// without a window.
/// </summary>
public static class SetAsideText
{
    /// <summary>Builds the dialog text for <paramref name="paths"/>.</summary>
    public static string For(IReadOnlyList<GamePath> paths)
    {
        var list = string.Join('\n', paths.Select(p => "* " + MarkdownText.Code(p.Path.ToString())));
        return $"These files leave the game folder:\n\n{list}\n\n"
            + $"They move into the group \"{ISetAsideFiles.GroupName}\", which stays turned off, and the loadout is applied now, including any other changes waiting to be applied. "
            + "The files go to the trash, or are deleted when the trash is on another drive; the app keeps its own copy, so turn the group on and apply to put them back.";
    }
}
