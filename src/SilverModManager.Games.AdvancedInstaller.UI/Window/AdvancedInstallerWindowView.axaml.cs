using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.AdvancedInstaller.UI.Resources;
using ReactiveUI;

namespace SilverModManager.Games.AdvancedInstaller.UI;

public partial class AdvancedInstallerWindowView : ReactiveWindow<IAdvancedInstallerWindowViewModel>
{
    public AdvancedInstallerWindowView()
    {
        InitializeComponent();
        GlassAppearance.AttachShared(this, "SurfaceLow");

        this.WhenActivated(disposables =>
        {
            Title = Language.OverlayHeaderText_Advanced_Installer;
            
            this.WhenAnyObservable(view => view.ViewModel!.UnsupportedModVM.DeclineCommand,
                    view => view.ViewModel!.AdvancedInstallerVM.FooterViewModel.CancelCommand)
                .Do(_ => Close())
                .Subscribe()
                .DisposeWith(disposables);

            this.WhenAnyObservable(view => view.ViewModel!.AdvancedInstallerVM.FooterViewModel.InstallCommand)
                .Do(_ =>
                {
                    ViewModel!.AdvancedInstallerVM.ShouldInstall = true;
                    Close();
                })
                .Subscribe()
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.CurrentPageVM,
                    v => v.CurrentPage.ViewModel)
                .DisposeWith(disposables);
        });
    }
}
