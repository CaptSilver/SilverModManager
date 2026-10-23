using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.About;

[UsedImplicitly]
public partial class AboutPageView : ReactiveUserControl<IAboutPageViewModel>
{
    public AboutPageView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.MarkdownRendererViewModel, view => view.ViewModelViewHost.ViewModel)
                .DisposeWith(disposables);
        });
    }
}
