using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.AdvancedInstaller.UI.Resources;
using SilverModManager.UI.Sdk;
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
            
            this.WhenViewModel(vm => vm.WhenAnyObservable(
                    x => x.UnsupportedModVM.DeclineCommand,
                    x => x.AdvancedInstallerVM.FooterViewModel.CancelCommand))
                .Do(_ => Close())
                .Subscribe()
                .DisposeWith(disposables);

            this.WhenViewModel(vm => vm.WhenAnyObservable(x => x.AdvancedInstallerVM.FooterViewModel.InstallCommand).Select(_ => vm))
                .Do(vm =>
                {
                    vm.AdvancedInstallerVM.ShouldInstall = true;
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
