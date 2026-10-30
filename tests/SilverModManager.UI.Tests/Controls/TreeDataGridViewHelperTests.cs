using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using DynamicData;
using FluentAssertions;
using R3;
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

    /// <summary>One root row, so the grid realises a row to prepare and clear; records the drag calls it receives.</summary>
    private sealed class RowAdapter : TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>
    {
        private readonly SourceCache<CompositeItemModel<EntityId>, EntityId> _roots = new(static model => model.Key);

        public List<(CompositeItemModel<EntityId> Model, bool Activating)> Activations { get; } = [];
        public List<string> Drags { get; } = [];

        public RowAdapter(IServiceProvider provider) : base(provider)
        {
            var root = new CompositeItemModel<EntityId>(EntityId.From(0x200000000000001));
            root.Add(SharedColumns.Name.NameComponentKey, new NameComponent(value: "row"));
            _roots.AddOrUpdate(root);
            ModelActivationSubject.Subscribe(Activations, static (activation, list) => list.Add(activation));
        }

        protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical)
            => _roots.Connect();

        protected override Avalonia.Controls.Models.TreeDataGrid.IColumn<CompositeItemModel<EntityId>>[] CreateColumns(bool viewHierarchical) =>
        [
            ITreeDataGridItemModel<CompositeItemModel<EntityId>, EntityId>.CreateExpanderColumn(ColumnCreator.Create<EntityId, SharedColumns.Name>()),
        ];

        public override void OnRowDragStarted(object? sender, TreeDataGridRowDragStartedEventArgs e) => Drags.Add("started");
        public override void OnRowDragOver(object? sender, TreeDataGridRowDragEventArgs e) => Drags.Add("over");
        public override void OnRowDrop(object? sender, TreeDataGridRowDragEventArgs e) => Drags.Add("drop");
    }

    private sealed class GridView : ReactiveUserControl<FakeViewModel>
    {
        public Avalonia.Controls.TreeDataGrid Grid { get; } = new();

        public GridView(Func<FakeViewModel, TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>> getAdapter, bool dragAndDrop = false)
        {
            Content = Grid;
            TreeDataGridViewHelper.SetupTreeDataGridAdapter(this, Grid, getAdapter, dragAndDrop);
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

    private static (List<FakeViewModel> Requested, RowAdapter Adapter, GridView View) RowGrid(IServiceProvider provider, bool dragAndDrop, FakeViewModel? viewModel)
    {
        var requested = new List<FakeViewModel>();
        var adapter = new RowAdapter(provider);
        var view = new GridView(vm => { requested.Add(vm); return adapter; }, dragAndDrop) { ViewModel = viewModel };
        return (requested, adapter, view);
    }

    // The grid shows the adapter's rows whichever view model the view has, as it does while a view is recycled.
    private async Task<TreeDataGridRow> ShowRows(GridView view, RowAdapter adapter)
    {
        _ = adapter.Activate(); // lives as long as the adapter, which the test drops
        await EventuallyOnUi(() => adapter.Source.Value.Rows.Count.Should().Be(1));
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            view.Grid.Source = adapter.Source.Value;
            var window = Track(new Window { Width = 400, Height = 200, Content = view });
            ShowRendered(window);
            Render(window);
            var row = view.Grid.TryGetRow(0);
            row.Should().NotBeNull("the grid realised its only row");
            return row!;
        });
    }

    private static void RaiseRowEvent(Avalonia.Controls.TreeDataGrid grid, string method, TreeDataGridRow row)
    {
        var raise = typeof(Avalonia.Controls.TreeDataGrid).GetMethod(method,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        raise.Should().NotBeNull($"the grid has {method}");
        raise!.Invoke(grid, [row, 0]);
    }

    [Fact]
    public async Task RowPreparationAndClearing_WithoutAViewModel_ReachNoAdapter()
    {
        var (requested, adapter, view) = await Dispatcher.UIThread.InvokeAsync(() => RowGrid(Provider, dragAndDrop: false, viewModel: null));
        var row = await ShowRows(view, adapter);

        await OnUi(() =>
        {
            RaiseRowEvent(view.Grid, "RaiseRowPrepared", row);
            RaiseRowEvent(view.Grid, "RaiseRowClearing", row);
        });

        requested.Should().BeEmpty();
        adapter.Activations.Should().BeEmpty();
    }

    [Fact]
    public async Task RowPreparationAndClearing_WithAViewModel_ReachTheAdapterInOrder()
    {
        var viewModel = new FakeViewModel();
        var (requested, adapter, view) = await Dispatcher.UIThread.InvokeAsync(() => RowGrid(Provider, dragAndDrop: false, viewModel));
        var row = await ShowRows(view, adapter);
        adapter.Activations.Clear();

        await OnUi(() =>
        {
            RaiseRowEvent(view.Grid, "RaiseRowPrepared", row);
            RaiseRowEvent(view.Grid, "RaiseRowClearing", row);
        });

        requested.Should().NotBeEmpty().And.OnlyContain(vm => vm == viewModel);
        adapter.Activations.Select(activation => activation.Activating).Should().Equal(true, false);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("over")]
    [InlineData("drop")]
    public async Task DragEvents_AreForwardedToTheAdapterOnlyWhileThereIsAViewModel(string recorded)
    {
        var viewModel = new FakeViewModel();
        var (withoutRequests, withoutAdapter, withoutView) = await Dispatcher.UIThread.InvokeAsync(() => RowGrid(Provider, dragAndDrop: true, viewModel: null));
        var withoutRow = await ShowRows(withoutView, withoutAdapter);
        await OnUi(() => RaiseDrag(withoutView.Grid, withoutRow, recorded));
        withoutRequests.Should().BeEmpty();
        withoutAdapter.Drags.Should().BeEmpty();

        var (_, adapter, view) = await Dispatcher.UIThread.InvokeAsync(() => RowGrid(Provider, dragAndDrop: true, viewModel));
        var row = await ShowRows(view, adapter);
        await OnUi(() => RaiseDrag(view.Grid, row, recorded));
        adapter.Drags.Should().Equal(recorded);
    }

    private static void RaiseDrag(Avalonia.Controls.TreeDataGrid grid, TreeDataGridRow row, string kind)
    {
        RoutedEventArgs args = kind switch
        {
            "started" => new TreeDataGridRowDragStartedEventArgs([]),
            "over" => new TreeDataGridRowDragEventArgs(Avalonia.Controls.TreeDataGrid.RowDragOverEvent, row, null),
            _ => new TreeDataGridRowDragEventArgs(Avalonia.Controls.TreeDataGrid.RowDropEvent, row, null),
        };
        grid.RaiseEvent(args);
    }
}
