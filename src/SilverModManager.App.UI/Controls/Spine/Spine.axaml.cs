using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Spine;

public partial class Spine : ReactiveUserControl<ISpineViewModel>
{
    private const string DragFormat = "SilverModManager.SpineLoadoutIndex";
    private const double DragThreshold = 6;

    private Point? _pressPoint;
    private int _pressedIndex = -1;
    private bool _isDragging;

    public Spine()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Home, v => v.Home.ViewModel)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.LoadoutSpineItems, v => v.Loadouts.ItemsSource)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Downloads, v => v.Download.ViewModel)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.AddLoadout, v => v.Add.ViewModel)
                .DisposeWith(disposables);

            // The icons are buttons that mark their pointer events handled, so the press has to be
            // watched on the way down. A click without movement never starts a drag and still selects.
            Loadouts.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            Loadouts.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            Loadouts.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            DragDrop.SetAllowDrop(Loadouts, true);
            Loadouts.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            Loadouts.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            Loadouts.AddHandler(DragDrop.DropEvent, OnDrop);

            Disposable.Create(() =>
            {
                Loadouts.RemoveHandler(PointerPressedEvent, OnPointerPressed);
                Loadouts.RemoveHandler(PointerMovedEvent, OnPointerMoved);
                Loadouts.RemoveHandler(PointerReleasedEvent, OnPointerReleased);
                Loadouts.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
                Loadouts.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
                Loadouts.RemoveHandler(DragDrop.DropEvent, OnDrop);
            }).DisposeWith(disposables);
        });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Loadouts).Properties.IsLeftButtonPressed) return;
        _pressedIndex = IndexOfItemUnder(e.Source);
        _pressPoint = _pressedIndex < 0 ? null : e.GetPosition(Loadouts);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is not { } start || _isDragging) return;

        var delta = e.GetPosition(Loadouts) - start;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        _isDragging = true;
        var index = _pressedIndex;
        _pressPoint = null;
        try
        {
            var data = new DataObject();
            data.Set(DragFormat, index);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _isDragging = false;
            DropIndicator.IsVisible = false;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _pressPoint = null;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DragFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        var slot = SlotAt(e.GetPosition(Loadouts));
        DropIndicator.Margin = new Thickness(0, Math.Max(0, SlotY(slot) - DropIndicator.Height / 2), 0, 0);
        DropIndicator.IsVisible = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropIndicator.IsVisible = false;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropIndicator.IsVisible = false;
        if (ViewModel is not { } viewModel || e.Data.Get(DragFormat) is not int from) return;

        var slot = SlotAt(e.GetPosition(Loadouts));
        // The slot counts the dragged icon itself; once it is lifted out, later slots shift down by one.
        var to = slot > from ? slot - 1 : slot;
        if (to != from) viewModel.MoveLoadout(from, to);
    }

    private IReadOnlyList<Control> Icons => Loadouts.ItemsPanelRoot?.Children ?? [];

    private int IndexOfItemUnder(object? source)
    {
        var icons = Icons;
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            var index = icons.ToList().FindIndex(icon => ReferenceEquals(icon, visual));
            if (index >= 0) return index;
        }
        return -1;
    }

    /// <summary>
    /// The insertion slot (0 = before the first icon, n = after the last) closest to <paramref name="point"/>.
    /// </summary>
    private int SlotAt(Point point)
    {
        var slot = 0;
        foreach (var icon in Icons)
        {
            if (point.Y > icon.Bounds.Y + icon.Bounds.Height / 2) slot++;
        }
        return slot;
    }

    private double SlotY(int slot)
    {
        var icons = Icons;
        if (icons.Count == 0) return 0;
        return slot < icons.Count ? icons[slot].Bounds.Y : icons[^1].Bounds.Bottom;
    }
}
