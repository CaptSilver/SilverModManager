using System.Collections.Immutable;
using System.Text;
using Avalonia.Threading;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// The Fix button of a game folder left by another mod manager: shows the steps, and once the user confirms runs
/// them as a job. A step that needs root asks for the password (or shows the command when it cannot); the folder is
/// planned again afterwards and only a plan identical to the confirmed one is applied, anything else is shown for
/// confirmation again. Health Check is refreshed after every run so the diagnostic clears when it is fixed.
/// </summary>
[UsedImplicitly]
public sealed class FixCleanupActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => GameCleanupActions.FixId;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var loadout = Loadout.Load(serviceProvider.GetRequiredService<IConnection>().Db, loadoutId);
        if (!loadout.IsValid()) throw new InvalidOperationException($"The loadout {loadoutId} no longer exists.");

        var fixer = serviceProvider.GetRequiredService<ICleanupFixService>();

        // Planning reads the game folder and Steam's files, so it stays off the UI thread.
        var plan = await Task.Run(() => fixer.Plan(loadout));
        string? note = null;
        var offered = new HashSet<string>(StringComparer.Ordinal);
        var saved = SaveReport.None;
        while (true)
        {
            if (!plan.Steps.Any(s => s.Acts || s.Kind == CleanupStepKind.Privileged))
            {
                await MessageBoxOkViewModel.Show(serviceProvider, "Nothing to fix", "The game folder has nothing left that the cleanup can change.", Markdown(CleanupPlanText.StepLines(plan)));
                return;
            }

            if (!await Confirm(plan, note)) return;

            // A changed plan is offered again only when it deletes something the earlier offers did not cover, so a
            // re-confirmation does not ask twice but a folder that appeared meanwhile is not lost. Nothing below deletes
            // until this returns.
            var deletes = plan.Steps.Where(s => s.Kind == CleanupStepKind.Delete).Select(s => s.Source).OfType<string>().ToArray();
            if (deletes.Any(d => !offered.Contains(d)))
            {
                offered.UnionWith(deletes);
                var save = await SaveModsFirst(loadout, plan);
                if (!save.Proceed) return;
                saved = saved.Plus(save.Report);
            }

            var outcome = await RunAsJob(loadoutId, loadout, fixer, plan);
            await diagnosticManager.RefreshAsync(loadoutId);

            switch (outcome.Status)
            {
                case CleanupFixStatus.PlanChanged:
                    plan = outcome.NewPlan ?? throw new InvalidOperationException("A changed plan came back without the new steps.");
                    note = "The game folder changed while the cleanup was waiting, so nothing was applied. These are the steps it needs now.";
                    break;
                case CleanupFixStatus.Cancelled:
                    await MessageBoxOkViewModel.Show(serviceProvider, "Cleanup did not run", outcome.Message ?? "Nothing was changed.");
                    return;
                default:
                    await ShowResult(outcome.Result ?? throw new InvalidOperationException("An applied cleanup came back without a result."), saved);
                    return;
            }
        }
    }

    /// <summary>Offers the mods the cleanup would delete for download into the library, and downloads the ones the user keeps ticked.</summary>
    private async Task<SaveOutcome> SaveModsFirst(Loadout.ReadOnly loadout, CleanupPlan plan)
    {
        var savePlan = await serviceProvider.GetRequiredService<ILibrarySaver>().PlanAsync(loadout, plan);
        return await SaveBeforeCleanup.RunAsync(savePlan, serviceProvider.GetRequiredService<IModDownloader>(), new SaveToLibraryPrompts(serviceProvider, windowManager), CancellationToken.None);
    }

    private async Task<CleanupFixOutcome> RunAsJob(LoadoutId loadoutId, Loadout.ReadOnly loadout, ICleanupFixService fixer, CleanupPlan plan)
    {
        var monitor = serviceProvider.GetRequiredService<IJobMonitor>();
        return await monitor.Begin(new CleanupFixJob(loadoutId), async context =>
        {
            await context.YieldAsync();
            return await fixer.FixAsync(loadout, plan, AskUserToRun, context.CancellationToken);
        });
    }

    private async Task<bool> Confirm(CleanupPlan plan, string? note)
    {
        var text = new StringBuilder();
        if (note is not null) text.AppendLine(note).AppendLine();
        text.AppendLine("The cleanup will do the following. Nothing outside the game folder and this loadout is touched.").AppendLine();
        foreach (var line in CleanupPlanText.StepLines(plan)) text.AppendLine(line);

        var dialog = DialogFactory.CreateStandardDialog(
            title: "Fix the game folder?",
            new StandardDialogParameters { Text = text.ToString() },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Fix", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ],
            windowSize: DialogWindowSize.Large);

        var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        return result.ButtonId == ButtonDefinitionId.Accept;
    }

    /// <summary>
    /// Shown when the password prompt cannot run: the exact command with a copy button, asked again until the
    /// attribute is gone or the user cancels.
    /// </summary>
    private async Task<bool> AskUserToRun(string command, string? why, Func<bool> isCleared, CancellationToken cancellationToken)
    {
        while (!isCleared())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The fix calls this from its job, which runs on the thread pool; Avalonia only builds windows on the UI thread.
            var shownWhy = why;
            var result = await Dispatcher.UIThread.InvokeAsync(() => windowManager.ShowDialog(ManualCommandDialog(command, shownWhy), DialogWindowType.Modal));
            if (result.ButtonId != ButtonDefinitionId.Accept) return false;

            why = "The attribute is still set";
        }

        return true;
    }

    private IDialog ManualCommandDialog(string command, string? why)
    {
        var markdown = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdown.Contents = $"```\n{command}\n```";
        return DialogFactory.CreateStandardDialog(
            title: "Run this command",
            new StandardDialogParameters
            {
                Text = $"{(why is null ? "" : why + ". ")}The app could not ask for your password, so run this in a terminal and press Continue.",
                Markdown = markdown,
                ShowMarkdownCopyButton = true,
            },
            buttonDefinitions:
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition("Continue", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Default),
            ]);
    }

    private async Task ShowResult(CleanupResult result, SaveReport saved)
    {
        var lines = result.Done.Select(step => "- " + step.Description).ToList();
        lines.AddRange(saved.Added.Select(m => $"- Added {m.Name} {m.Version} to your library"));
        lines.AddRange(saved.OpenedInBrowser.Select(m => $"- Opened the page of {m.Name} {m.Version} in your browser; download it there to add it to your library"));
        lines.AddRange(saved.Failed.Select(f => $"- Could not add {f.Mod.Name} {f.Mod.Version} to your library: {f.Message}"));
        if (result.RestoreRecordFolder is { } folder) lines.Add($"- The removed links are recorded in {folder}");

        if (result.Failure is { } failure)
            await MessageBoxOkViewModel.Show(serviceProvider, "Cleanup stopped", failure, lines.Count > 0 ? "Done so far:\n\n" + string.Join('\n', lines) : null);
        else
            await MessageBoxOkViewModel.Show(serviceProvider, "Cleanup finished", "The game folder is back to the Steam install.", string.Join('\n', lines));
    }

    private static string Markdown(IEnumerable<string> lines) => string.Join("\n\n", lines);
}
