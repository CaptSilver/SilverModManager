using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// A button on the details page that runs one of the diagnostic's fix actions.
/// </summary>
public sealed record DiagnosticActionButton(string Label, ReactiveCommand<Unit, Unit> Command, string? Note = null, IObservable<string?>? status = null)
{
    /// <summary>What the action is doing while it runs, shown next to the button; null when idle.</summary>
    public IObservable<string?> Status { get; } = status ?? Observable.Return<string?>(null);
}

public class DiagnosticDetailsViewModel : APageViewModel<IDiagnosticDetailsViewModel>, IDiagnosticDetailsViewModel
{
    public DiagnosticSeverity Severity { get; }

    public IMarkdownRendererViewModel MarkdownRendererViewModel { get; }

    public IMarkdownRendererViewModel? MoreDetailsRendererViewModel { get; }

    public IReadOnlyList<DiagnosticActionButton> Actions { get; }

    public DiagnosticDetailsViewModel(
        IWindowManager windowManager,
        IDiagnosticWriter diagnosticWriter,
        IMarkdownRendererViewModel markdownRendererViewModel,
        Diagnostic diagnostic,
        LoadoutId loadoutId,
        IEnumerable<IDiagnosticActionHandler> actionHandlers,
        ILogger<DiagnosticDetailsViewModel> logger,
        IMarkdownRendererViewModel? moreDetailsRendererViewModel = null) : base(windowManager)
    {
        TabIcon = IconValues.Cardiology;
        TabTitle = diagnostic.Title;
        Severity = diagnostic.Severity;

        var summary = diagnostic.FormatSummary(diagnosticWriter);
        var details = $"## {summary}\n" +
                  $"{diagnostic.FormatDetails(diagnosticWriter)}";

        MarkdownRendererViewModel = markdownRendererViewModel;
        MarkdownRendererViewModel.Contents = details;

        if (!string.IsNullOrWhiteSpace(diagnostic.MoreDetails))
        {
            MoreDetailsRendererViewModel = moreDetailsRendererViewModel
                ?? throw new ArgumentNullException(nameof(moreDetailsRendererViewModel), "The diagnostic has more details, so the page needs a renderer for them.");
            MoreDetailsRendererViewModel.Contents = diagnostic.MoreDetails;
        }

        Actions = CreateButtons(diagnostic, loadoutId, actionHandlers.ToArray(), logger);
    }

    private static IReadOnlyList<DiagnosticActionButton> CreateButtons(Diagnostic diagnostic, LoadoutId loadoutId, IDiagnosticActionHandler[] handlers, ILogger logger)
    {
        if (diagnostic.Actions.IsDefaultOrEmpty) return [];

        var buttons = new List<DiagnosticActionButton>();
        foreach (var action in diagnostic.Actions)
        {
            var handler = handlers.FirstOrDefault(h => h.ActionId == action.Id);
            if (handler is null)
            {
                logger.LogWarning("Diagnostic {DiagnosticId} offers the action {ActionId}, which has no handler, so it gets no button", diagnostic.Id, action.Id);
                continue;
            }

            var command = ReactiveCommand.CreateFromTask(async () =>
            {
                try
                {
                    await handler.ExecuteAsync(loadoutId, action.Arguments);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "The diagnostic action {ActionId} failed for loadout {LoadoutId}", action.Id, loadoutId);
                }
            });
            buttons.Add(new DiagnosticActionButton(action.Label, command, action.Note, handler.StatusOf(loadoutId)));
        }

        return buttons;
    }
}
