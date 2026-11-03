using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

public partial class ApplyDiffView : ReactiveUserControl<IApplyDiffViewModel>
{
    public ApplyDiffView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
            {
                this.OneWayBind(ViewModel, vm => vm.BodyViewModel, v => v.TreeViewHost.ViewModel)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.LoadFailed, v => v.FailedText.IsVisible)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.StrayFiles, v => v.StrayList.ItemsSource)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.StrayFiles, v => v.StrayNotice.IsVisible, files => files.Count > 0)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.RemoveStrayFilesCommand, v => v.RemoveStrayButton)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.RefreshCommand, v => v.RefreshButton)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.RemoveSelectedCommand, v => v.RemoveButton)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.RemoveSelectedCommand, v => v.MenuItemRemove)
                    .DisposeWith(d);

                // The Delete key reaches this grid from anywhere inside the tree.
                if (ViewModel is { } viewModel)
                    RootGrid.KeyBindings[0].Command = viewModel.RemoveSelectedCommand;
            }
        );
    }
}

