using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Takes a file's row out of the one group the diagnostic names, after asking. Scoped to that group: when a mod places the
/// same file, its row stays and its copy is what the next apply deploys.
/// </summary>
[UsedImplicitly]
public sealed class RemoveLoadoutFileActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.RemoveLoadoutFileId;

    /// <inheritdoc />
    public string? LabelFor(DiagnosticAction action) => Language.DiagnosticAction_RemoveLoadoutFile;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var (path, group) = DiagnosticActions.ParseRemoveLoadoutFile(arguments);
        await RemoveLoadoutFilesActionHandler.Run(serviceProvider, windowManager, diagnosticManager, loadoutId, [path], group);
    }
}

/// <summary>
/// The list form of <see cref="RemoveLoadoutFileActionHandler"/>: one question for every file of a finding. A path the
/// group has no row for (a file dropped in after the last apply) is skipped and named, not an error.
/// </summary>
[UsedImplicitly]
public sealed class RemoveLoadoutFilesActionHandler(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.RemoveLoadoutFilesId;

    /// <inheritdoc />
    public string? LabelFor(DiagnosticAction action) => Language.DiagnosticAction_RemoveLoadoutFile;

    /// <inheritdoc />
    public async Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var (paths, group) = DiagnosticActions.ParseRemoveLoadoutFiles(arguments);
        await Run(serviceProvider, windowManager, diagnosticManager, loadoutId, paths, group);
    }

    /// <summary>
    /// Removes <paramref name="paths"/> from <paramref name="group"/> through the shared flow and refreshes the diagnostics.
    /// </summary>
    internal static async Task Run(IServiceProvider serviceProvider, IWindowManager windowManager, IDiagnosticManager diagnosticManager,
        LoadoutId loadoutId, IReadOnlyList<GamePath> paths, LoadoutItemGroupId group)
    {
        var remover = serviceProvider.GetRequiredService<ILoadoutFileRemover>();
        var withRows = (await Task.Run(() => remover.Plan(new FileRemovalRequest(loadoutId, paths.ToArray(), OnlyInGroups: [group]))))
            .Rows.Select(r => r.Path).ToHashSet();
        var skipped = paths.Where(p => !withRows.Contains(p)).Distinct().ToArray();

        // With no row at all the flow's own refusal tells the user so; with some, only those go.
        var request = new FileRemovalRequest(loadoutId, withRows.Count == 0 ? paths.ToArray() : paths.Where(withRows.Contains).Distinct().ToArray(), OnlyInGroups: [group]);
        if (!await LoadoutFileRemovalFlow.Run(serviceProvider, windowManager, request, Language.ApplyDiff_Remove_Activity)) return;

        if (skipped.Length > 0 && withRows.Count > 0)
        {
            var names = string.Join(", ", skipped.Select(p => MarkdownText.Code(p.Path.ToString())));
            serviceProvider.GetRequiredService<IWindowNotificationService>().ShowToast(string.Format(Language.ApplyDiff_Remove_SkippedToast, names));
        }
        await diagnosticManager.RefreshAsync(loadoutId);
    }
}
