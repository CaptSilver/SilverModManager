using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.ReactiveUI;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Diagnostics;

public partial class DiagnosticEntryView : ReactiveUserControl<IDiagnosticEntryViewModel>
{
    public DiagnosticEntryView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.WhenAnyValue(view => view.ViewModel)
                .WhereNotNull()
                .Do(InitializeData)
                .Subscribe()
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.SeeDetailsCommand, view => view.EntryButton)
                .DisposeWith(d);
        });
    }

    private void InitializeData(IDiagnosticEntryViewModel vm)
    {
        // a recycled row gets a new view model, so set all three instead of only adding
        SeverityIcon.Classes.Set("Suggestion", vm.Severity == DiagnosticSeverity.Suggestion);
        SeverityIcon.Classes.Set("Warning", vm.Severity == DiagnosticSeverity.Warning);
        SeverityIcon.Classes.Set("Critical", vm.Severity == DiagnosticSeverity.Critical);

        switch (vm.Severity)
        {
            case DiagnosticSeverity.Suggestion:
                SeverityIcon.Value = IconValues.InfoFilled;
                break;
            case DiagnosticSeverity.Warning:
                SeverityIcon.Value = IconValues.Warning;
                break;
            case DiagnosticSeverity.Critical:
                SeverityIcon.Value = IconValues.Error;
                break;
            default:
                SeverityIcon.Value = IconValues.NotificationImportant;
                break;
        }
                
        DescriptionText.Text = vm.Summary;
        TitleText.Text = vm.Title;
    }
}
