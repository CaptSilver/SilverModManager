using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.Diagnostics;

public class DiagnosticDetailsDesignViewModel : APageViewModel<IDiagnosticDetailsViewModel>, IDiagnosticDetailsViewModel
{
    private const string Details = "This is an example diagnostic details, lots of stuff here.";
    public DiagnosticSeverity Severity => DiagnosticSeverity.Critical;

    public IMarkdownRendererViewModel MarkdownRendererViewModel => new MarkdownRendererViewModel
    {
        Contents = Details
    };

    public IReadOnlyList<DiagnosticActionButton> Actions => [];

    public DiagnosticDetailsDesignViewModel() : base(new DesignWindowManager()) { }

}
