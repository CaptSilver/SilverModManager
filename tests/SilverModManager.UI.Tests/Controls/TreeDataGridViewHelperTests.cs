using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using DynamicData;
using FluentAssertions;
using ReactiveUI;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests.Controls;

/// <summary>
/// Grid containers are recycled, so a view can be attached to a window while its view model is not set. Events the
/// helper listens to must read the view model when they fire and do nothing while there is none.
/// </summary>
public class TreeDataGridViewHelperTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FakeViewModel : ReactiveObject, IViewModelInterface
    {
        public ViewModelActivator Activator { get; } = new();
    }

    private sealed class EmptyAdapter(IServiceProvider provider)
        : TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>(provider)
    {
        protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical)
            => new SourceCache<CompositeItemModel<EntityId>, EntityId>(static model => model.Key).Connect();

        protected override Avalonia.Controls.Models.TreeDataGrid.IColumn<CompositeItemModel<EntityId>>[] CreateColumns(bool viewHierarchical) => [];
    }

    private sealed class GridView : ReactiveUserControl<FakeViewModel>
    {
        public Avalonia.Controls.TreeDataGrid Grid { get; } = new();

        public GridView(Func<FakeViewModel, TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>> getAdapter)
        {
            Content = Grid;
            TreeDataGridViewHelper.SetupTreeDataGridAdapter(this, Grid, getAdapter);
        }
    }

    private static void PressCtrlA(Avalonia.Controls.TreeDataGrid grid)
    {
        grid.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.A,
            KeyModifiers = KeyModifiers.Control,
            Source = grid,
        });
    }

    [Fact]
    public async Task SelectAllShortcut_WithoutAViewModel_DoesNothing()
    {
        var requested = new List<FakeViewModel>();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var adapter = new EmptyAdapter(Provider);
            var view = new GridView(vm => { requested.Add(vm); return adapter; });
            ShowRendered(Track(new Window { Width = 400, Height = 200, Content = view }));
            PressCtrlA(view.Grid);
        });

        requested.Should().BeEmpty();
    }

    [Fact]
    public async Task SelectAllShortcut_WithAViewModel_AsksForItsAdapter()
    {
        var requested = new List<FakeViewModel>();
        var viewModel = new FakeViewModel();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var adapter = new EmptyAdapter(Provider);
            var view = new GridView(vm => { requested.Add(vm); return adapter; }) { ViewModel = viewModel };
            ShowRendered(Track(new Window { Width = 400, Height = 200, Content = view }));
            PressCtrlA(view.Grid);
        });

        requested.Should().Equal(viewModel);
    }

    [Fact]
    public async Task Deactivation_WithoutAViewModel_DoesNothing()
    {
        var requested = new List<FakeViewModel>();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var adapter = new EmptyAdapter(Provider);
            var view = new GridView(vm => { requested.Add(vm); return adapter; });
            var window = Track(new Window { Width = 400, Height = 200, Content = view });
            ShowRendered(window);
            window.Close();
        });

        requested.Should().BeEmpty();
    }
}
