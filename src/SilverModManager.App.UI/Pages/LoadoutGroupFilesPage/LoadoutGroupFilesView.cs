using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using SilverModManager.App.UI.Controls;
using SilverModManager.Sdk.Games;
using R3;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

[UsedImplicitly]
public partial class LoadoutGroupFilesView : ReactiveUserControl<ILoadoutGroupFilesViewModel>
{
    public LoadoutGroupFilesView()
    {
        InitializeComponent();
        TreeDataGridViewHelper.SetupTreeDataGridAdapter<LoadoutGroupFilesView, ILoadoutGroupFilesViewModel, CompositeItemModel<GamePath>, GamePath>(this, TreeDataGrid, vm => vm.FileTreeAdapter!);

        this.WhenActivated(disposables =>
        {
            SearchControl.AttachKeyboardHandlers(this, disposables);

            // Bind search adapter
            this.OneWayBind(ViewModel, vm => vm.FileTreeAdapter, view => view.SearchControl.Adapter)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.FileTreeAdapter!.Source.Value, view => view.TreeDataGrid.Source)
                .AddTo(disposables);
            
            this.BindCommand(ViewModel, vm => vm.OpenEditorCommand, view => view.OpenEditorMenuItem)
                .AddTo(disposables);
            
            this.BindCommand(ViewModel, vm => vm.RemoveCommand, view => view.RemoveButton)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.UndoTreatAsMineCommand, view => view.UndoTreatAsMineButton)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.MoveToNewModCommand, view => view.MoveToNewModButton)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.MoveToExistingModCommand, view => view.MoveToExistingModButton)
                .AddTo(disposables);

            // Hidden, not just disabled: on a mod's own file list there is no External Changes to move out of.
            this.OneWayBind(ViewModel, vm => vm.IsExternalChanges, view => view.MoveToNewModButton.IsVisible)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.IsExternalChanges, view => view.MoveToExistingModButton.IsVisible)
                .AddTo(disposables);
        });
    }
}

