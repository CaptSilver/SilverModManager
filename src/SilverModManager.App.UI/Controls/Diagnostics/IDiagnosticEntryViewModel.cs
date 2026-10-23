using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Diagnostics;

public interface IDiagnosticEntryViewModel : IViewModelInterface
{
    Diagnostic Diagnostic { get; }

    string Title { get; }
    
    string Summary { get; }
    
    DiagnosticSeverity Severity { get; }
    
    ReactiveCommand<NavigationInformation, ValueTuple<Diagnostic, NavigationInformation>> SeeDetailsCommand { get; }
}

