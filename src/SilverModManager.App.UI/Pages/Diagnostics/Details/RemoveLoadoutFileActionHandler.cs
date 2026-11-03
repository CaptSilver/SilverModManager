using System.Collections.Immutable;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Pages.Diff.ApplyDiff;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
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
        var request = new FileRemovalRequest(loadoutId, [path], OnlyInGroups: [group]);
        if (await LoadoutFileRemovalFlow.Run(serviceProvider, windowManager, request, Language.ApplyDiff_Remove_Activity))
            await diagnosticManager.RefreshAsync(loadoutId);
    }
}
