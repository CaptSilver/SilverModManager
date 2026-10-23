using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.WorkspaceSystem;

[UsedImplicitly]
public partial class NewTabPageView : ReactiveUserControl<INewTabPageViewModel>
{
    public NewTabPageView()
    {
        InitializeComponent();

        this.WhenActivated(disposable =>
        {
            this.OneWayBind(ViewModel, vm => vm.AlertSettingsWrapper, view => view.InfoAlert.AlertSettings)
                .DisposeWith(disposable);
            
            this.OneWayBind(ViewModel, vm => vm.Sections, view => view.Sections.ItemsSource)
                 .DisposeWith(disposable);
        });
    }
}
