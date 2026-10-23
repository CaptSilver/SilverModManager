using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.Diagnostics;

public interface IDiagnosticDetailsViewModel : IPageViewModelInterface
{
    DiagnosticSeverity Severity { get; }

    IMarkdownRendererViewModel MarkdownRendererViewModel { get; }

    /// <summary>
    /// The diagnostic's fixes that have a handler, shown as buttons.
    /// </summary>
    IReadOnlyList<DiagnosticActionButton> Actions { get; }
}
