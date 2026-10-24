using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Spine;

public partial class Spine : ReactiveUserControl<ISpineViewModel>
{
    private const double LiftScale = 1.15;
    private const double LiftedOpacity = 0.92;
    private static readonly TimeSpan DefaultSlideDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DefaultLiftDuration = TimeSpan.FromMilliseconds(120);

    private Point? _pressPoint;
    private int _pressedIndex = -1;
    private DragState? _drag;
    private bool _isSettling;

    /// <summary>Layout snapshot and live position of the tile being dragged.</summary>
    private sealed class DragState
    {
        public required int From { get; init; }
        public required Control Tile { get; init; }
        public required double StartY { get; init; }
        public required double Stride { get; init; }
        public required double[] Tops { get; init; }
        public required double[] Heights { get; init; }
        public required IPointer Pointer { get; init; }
        public int Target { get; set; }
        public double Offset { get; set; }
    }

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
            // The drag is done with pointer capture rather than DragDrop: it is a reorder within one
            // list, and DragDrop would show the OS drag cursor instead of letting the tile itself move.
            Loadouts.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            Loadouts.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            Loadouts.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            Loadouts.PointerCaptureLost += OnPointerCaptureLost;
            Loadouts.ItemsView.CollectionChanged += OnLoadoutsChanged;

            Disposable.Create(() =>
            {
                Loadouts.RemoveHandler(PointerPressedEvent, OnPointerPressed);
                Loadouts.RemoveHandler(PointerMovedEvent, OnPointerMoved);
                Loadouts.RemoveHandler(PointerReleasedEvent, OnPointerReleased);
                Loadouts.PointerCaptureLost -= OnPointerCaptureLost;
                Loadouts.ItemsView.CollectionChanged -= OnLoadoutsChanged;
                EndDrag();
            }).DisposeWith(disposables);
        });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isSettling || _drag != null) return;
        if (!e.GetCurrentPoint(Loadouts).Properties.IsLeftButtonPressed) return;
        _pressedIndex = IndexOfItemUnder(e.Source);
        _pressPoint = _pressedIndex < 0 ? null : e.GetPosition(Loadouts.ItemsPanelRoot);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is { } drag)
        {
            UpdateDrag(drag, e.GetPosition(Loadouts.ItemsPanelRoot).Y);
            e.Handled = true;
            return;
        }

        if (_pressPoint is not { } start || Loadouts.ItemsPanelRoot is not { } panel) return;
        var delta = e.GetPosition(panel) - start;
        if (!SpineDragLayout.ExceedsThreshold(delta.X, delta.Y)) return;

        var from = _pressedIndex;
        _pressPoint = null;
        BeginDrag(from, start.Y, e.Pointer);
        if (_drag is { } started) UpdateDrag(started, e.GetPosition(panel).Y);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressPoint = null;
        if (_drag is not { } drag) return;

        var releaseAt = e.GetPosition(Loadouts);
        e.Handled = true;
        _ = SettleAsync(drag, commit: SpineDragLayout.CommitsOnRelease(Loadouts.Bounds.Width, releaseAt.X, releaseAt.Y));
    }

    // Losing the capture (window deactivated, popup opened) must not leave a tile stuck in the air.
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag is { } drag && !_isSettling) _ = SettleAsync(drag, commit: false);
    }

    // A loadout added or removed mid-drag invalidates the indices and positions the drag captured,
    // so the drag is dropped rather than committing a move against the wrong tiles.
    private void OnLoadoutsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_drag != null) EndDrag();
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is not { } drag || _isSettling) return;
        e.Handled = true;
        _ = SettleAsync(drag, commit: false);
    }

    private void BeginDrag(int from, double startY, IPointer pointer)
    {
        var icons = Icons;
        if (icons.Count < 2 || from < 0 || from >= icons.Count) return;

        var tops = icons.Select(icon => icon.Bounds.Y).ToArray();
        var heights = icons.Select(icon => icon.Bounds.Height).ToArray();
        var gap = from + 1 < icons.Count
            ? tops[from + 1] - (tops[from] + heights[from])
            : tops[from] - (tops[from - 1] + heights[from - 1]);

        var tile = icons[from];
        _drag = new DragState
        {
            From = from,
            Tile = tile,
            StartY = startY,
            Stride = heights[from] + gap,
            Tops = tops,
            Heights = heights,
            Pointer = pointer,
            Target = from,
        };

        pointer.Capture(Loadouts);
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        for (var i = 0; i < icons.Count; i++)
        {
            if (i == from) continue;
            icons[i].Transitions = SlideTransitions(SlideDuration);
            icons[i].RenderTransform = Transform(0, 1);
        }

        // The lifted tile eases up to its raised look; once that is done it must track the pointer without lag.
        tile.ZIndex = 100;
        tile.Opacity = LiftedOpacity;
        tile.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 3, BlurRadius = 10, Color = Colors.Black, Opacity = 0.45 };
        tile.Transitions = SlideTransitions(LiftDuration);
        tile.RenderTransform = Transform(0, LiftScale);
        var state = _drag;
        DelayThen(LiftDuration, () =>
        {
            if (_drag != state || state is null) return;
            state.Tile.Transitions = null;
        });
    }

    private void UpdateDrag(DragState drag, double pointerY)
    {
        drag.Offset = SpineDragLayout.ClampOffset(drag.Tops, drag.Heights, drag.From, pointerY - drag.StartY);
        drag.Tile.RenderTransform = Transform(drag.Offset, LiftScale);

        var centre = drag.Tops[drag.From] + drag.Offset + drag.Heights[drag.From] / 2;
        var target = SpineDragLayout.TargetIndex(drag.Tops, drag.Heights, drag.From, centre);
        if (target == drag.Target) return;

        drag.Target = target;
        var icons = Icons;
        for (var i = 0; i < icons.Count; i++)
        {
            if (i == drag.From) continue;
            icons[i].RenderTransform = Transform(SpineDragLayout.NeighbourShift(i, drag.From, target, drag.Stride), 1);
        }
    }

    private async Task SettleAsync(DragState drag, bool commit)
    {
        if (_isSettling) return;
        _isSettling = true;
        try
        {
            var target = commit ? drag.Target : drag.From;
            if (!commit)
            {
                // Everything glides back to where it started.
                var icons = Icons;
                for (var i = 0; i < icons.Count; i++)
                {
                    if (i != drag.From) icons[i].RenderTransform = Transform(0, 1);
                }
            }

            drag.Tile.Transitions = SlideTransitions(SlideDuration);
            drag.Tile.Opacity = 1;
            drag.Tile.Effect = null;
            drag.Tile.RenderTransform = Transform(SpineDragLayout.SettleOffset(drag.Tops, drag.Heights, drag.From, target), 1);

            await Task.Delay(SlideDuration);
            if (_drag != drag) return;

            // Drop the transforms first so the new order is not painted with them still applied.
            EndDrag();
            if (commit && target != drag.From) ViewModel?.MoveLoadout(drag.From, target);
        }
        finally
        {
            _isSettling = false;
        }
    }

    private void EndDrag()
    {
        var drag = _drag;
        _drag = null;
        _pressPoint = null;
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);

        foreach (var icon in Icons)
        {
            icon.Transitions = null;
            icon.RenderTransform = null;
            icon.ZIndex = 0;
            icon.Opacity = 1;
            icon.Effect = null;
        }

        if (drag?.Pointer.Captured == Loadouts) drag.Pointer.Capture(null);
    }

    private TimeSpan SlideDuration => ThemeDuration("SpineReorderSlideDuration", DefaultSlideDuration);
    private TimeSpan LiftDuration => ThemeDuration("SpineReorderLiftDuration", DefaultLiftDuration);

    private TimeSpan ThemeDuration(string key, TimeSpan fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is TimeSpan duration ? duration : fallback;

    private static Transitions SlideTransitions(TimeSpan duration) =>
    [
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = duration,
            Easing = new CubicEaseOut(),
        },
    ];

    // Every tile always carries the same two operations so a transition can interpolate between any two of them.
    private static ITransform Transform(double offsetY, double scale)
    {
        var builder = TransformOperations.CreateBuilder(2);
        builder.AppendTranslate(0, offsetY);
        builder.AppendScale(scale, scale);
        return builder.Build();
    }

    private static async void DelayThen(TimeSpan delay, Action action)
    {
        await Task.Delay(delay);
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
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
}
