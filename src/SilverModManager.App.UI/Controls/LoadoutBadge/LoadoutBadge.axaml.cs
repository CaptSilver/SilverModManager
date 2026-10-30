using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.ReactiveUI;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.LoadoutBadge;

[PseudoClasses(":selected", ":in-progress", ":applied")]
public partial class LoadoutBadge : ReactiveUserControl<ILoadoutBadgeViewModel>
{
    public LoadoutBadge()
    {
        InitializeComponent();

        this.WhenActivated(d =>
            {
                this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsLoadoutSelected))
                    .OnUI()
                    .Subscribe(SetSelected)
                    .DisposeWith(d);

                this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsLoadoutApplied))
                    .OnUI()
                    .Subscribe(SetApplied)
                    .DisposeWith(d);

                this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsLoadoutInProgress))
                    .OnUI()
                    .Subscribe(SetInProgress)
                    .DisposeWith(d);
                
                this.OneWayBind(ViewModel, vm => vm.LoadoutShortName, v => v.ShortName.Text)
                    .DisposeWith(d);
                
                this.OneWayBind(ViewModel, vm => vm.IsVisible, v => v.IsVisible)
                    .DisposeWith(d);
            }
        );
    }
    
    private void SetSelected(bool selected)
    {
        PseudoClasses.Set(":selected", selected);
    }
    
    private void SetApplied(bool applied)
    {
        PseudoClasses.Set(":applied", applied);
    }
    
    private void SetInProgress(bool inProgress)
    {
        PseudoClasses.Set(":in-progress", inProgress);
    }
}

