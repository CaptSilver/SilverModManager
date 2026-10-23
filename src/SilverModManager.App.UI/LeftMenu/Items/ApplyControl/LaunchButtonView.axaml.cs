using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace NexusMods.App.UI.LeftMenu.Items;

public partial class LaunchButtonView : ReactiveUserControl<ILaunchButtonViewModel>
{
    public LaunchButtonView()
    {
        InitializeComponent();
        
        this.WhenActivated(d =>
        {
            var isRunning = ViewModel!.IsRunningObservable;
            var isNotRunning = ViewModel!.IsRunningObservable.Select(running => !running);

            isNotRunning.BindToUi(this, view => view.LaunchButton.IsEnabled)
                .DisposeWith(d);
            
            // Show progress bar when running
            isRunning.BindToUi(this, view => view.LaunchSpinner.IsVisible)
                .DisposeWith(d);
            
            // Show icon when not running
            isNotRunning.BindToUi(this, view => view.LaunchIcon.IsVisible)
                .DisposeWith(d);
            
            // Bind the 'launch' button.
            this.WhenAnyValue(view => view.ViewModel!.Command)
                .BindToUi(this, view => view.LaunchButton.Command)
                .DisposeWith(d);
            
            // Backing out while Steam has not started the game yet.
            this.WhenAnyValue(view => view.ViewModel)
                .WhereNotNull()
                .Select(viewModel => viewModel.WhenAnyValue(vm => vm.IsWaitingForSteam))
                .Switch()
                .BindToUi(this, view => view.CancelButton.IsVisible)
                .DisposeWith(d);

            this.WhenAnyValue(view => view.ViewModel)
                .WhereNotNull()
                .Select(viewModel => viewModel.CancelCommand)
                .BindToUi(this, view => view.CancelButton.Command)
                .DisposeWith(d);

            // Set the 'play' / 'running' text.
            this.WhenAnyValue(view => view.ViewModel!.Label)
                .BindToUi(this, view => view.LaunchText.Text)
                .DisposeWith(d);
        });
    }
}

