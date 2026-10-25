using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SilverModManager.App.UI.Controls.Spine;

namespace SilverModManager.App.UI.Controls;

/// <summary>
/// Lets the user drag the entries of a vertical <see cref="ItemsControl"/> into a new order: a press and drag lifts the
/// entry, its neighbours slide aside, the scroll viewer around the list scrolls when the pointer nears its edge, and on
/// release the entry settles into its slot and <c>move</c> is called with the old and new index. The slot arithmetic is
/// <see cref="SpineDragLayout"/>'s, as on the spine.
/// </summary>
/// <remarks>
/// Only a contiguous run of entries can move: the ones <c>isMovable</c> accepts. Entries before or after that run
/// stay where they are and are never slid. The drag is done with pointer capture rather than DragDrop, since it is a
/// reorder within one list and DragDrop would show the OS drag cursor instead of letting the entry itself move.
/// </remarks>
internal sealed class VerticalReorderDrag : IDisposable
{
    // The menu entries are as wide as their column, so the spine's 1.15 would push a lifted entry out of it.
    private const double LiftScale = 1.04;
    private const double LiftedOpacity = 0.92;
    private const double EdgeScrollBand = 48;
    private const double EdgeScrollMaxStep = 14;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan LiftDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan EdgeScrollInterval = TimeSpan.FromMilliseconds(16);

    private readonly ItemsControl _items;
    private readonly ScrollViewer _scroller;
    private readonly Func<int, bool> _isMovable;
    private readonly Action<int, int> _move;
    private readonly DispatcherTimer _edgeScrollTimer;

    private Point? _pressPoint;
    private int _pressedIndex = -1;
    private DragState? _drag;
    private bool _isSettling;

    // The panels around the list clip to their bounds, which would cut off the lifted entry's scaled edges and shadow;
    // clipping is switched off on that chain while an entry is lifted and put back afterwards.
    private readonly List<(Visual Visual, bool ClipToBounds)> _unclipped = [];

    /// <summary>Layout snapshot and live position of the entry being dragged. Indices in the arrays start at <see cref="First"/>.</summary>
    private sealed class DragState
    {
        /// <summary>Index in the list of the first entry that can move.</summary>
        public required int First { get; init; }
        /// <summary>Index within the movable run of the dragged entry.</summary>
        public required int From { get; init; }
        public required Control Tile { get; init; }
        public required double StartY { get; init; }
        public required double Stride { get; init; }
        public required double[] Tops { get; init; }
        public required double[] Heights { get; init; }
        public required IPointer Pointer { get; init; }
        public int Target { get; set; }
        public double Offset { get; set; }

        // Pointer Y in the item panel's coordinates (moves with the content when it scrolls) and in the scroll
        // viewer's (stays put); the timer needs both to scroll and then re-place the entry.
        public double PointerPanelY { get; set; }
        public double PointerViewportY { get; set; }
    }

    public VerticalReorderDrag(ItemsControl items, ScrollViewer scroller, Func<int, bool> isMovable, Action<int, int> move)
    {
        _items = items;
        _scroller = scroller;
        _isMovable = isMovable;
        _move = move;

        _edgeScrollTimer = new DispatcherTimer { Interval = EdgeScrollInterval };
        _edgeScrollTimer.Tick += OnEdgeScrollTick;

        // The entries are buttons that mark their pointer events handled, so the press has to be watched on the way
        // down. A click without movement never starts a drag and still opens the entry.
        items.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        items.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        items.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        items.PointerCaptureLost += OnPointerCaptureLost;
        items.ItemsView.CollectionChanged += OnItemsChanged;
    }

    public void Dispose()
    {
        _items.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _items.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _items.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        _items.PointerCaptureLost -= OnPointerCaptureLost;
        _items.ItemsView.CollectionChanged -= OnItemsChanged;
        EndDrag();
    }

    private IReadOnlyList<Control> Rows => _items.ItemsPanelRoot?.Children ?? [];

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isSettling || _drag != null) return;
        if (!e.GetCurrentPoint(_items).Properties.IsLeftButtonPressed) return;
        var index = IndexOfRowUnder(e.Source);
        _pressedIndex = _isMovable(index) ? index : -1;
        _pressPoint = _pressedIndex < 0 ? null : e.GetPosition(_items.ItemsPanelRoot);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is { } drag)
        {
            drag.PointerViewportY = e.GetPosition(_scroller).Y;
            UpdateDrag(drag, e.GetPosition(_items.ItemsPanelRoot).Y);
            e.Handled = true;
            return;
        }

        if (_pressPoint is not { } start || _items.ItemsPanelRoot is not { } panel) return;
        var delta = e.GetPosition(panel) - start;
        if (!SpineDragLayout.ExceedsThreshold(delta.X, delta.Y)) return;

        _pressPoint = null;
        BeginDrag(_pressedIndex, start.Y, e.Pointer);
        if (_drag is { } started)
        {
            started.PointerViewportY = e.GetPosition(_scroller).Y;
            UpdateDrag(started, e.GetPosition(panel).Y);
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressPoint = null;
        if (_drag is not { } drag) return;

        var releaseAt = e.GetPosition(_items);
        e.Handled = true;
        _ = SettleAsync(drag, commit: SpineDragLayout.CommitsOnRelease(_items.Bounds.Width, releaseAt.X, releaseAt.Y));
    }

    // Losing the capture (window deactivated, popup opened) must not leave an entry stuck in the air.
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag is { } drag && !_isSettling) _ = SettleAsync(drag, commit: false);
    }

    // An entry added or removed mid-drag invalidates the indices and positions the drag captured, so the drag is
    // dropped rather than committing a move against the wrong entries.
    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_drag != null) EndDrag();
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is not { } drag || _isSettling) return;
        e.Handled = true;
        _ = SettleAsync(drag, commit: false);
    }

    private void BeginDrag(int pressed, double startY, IPointer pointer)
    {
        var rows = Rows;
        var first = pressed;
        while (first > 0 && _isMovable(first - 1)) first--;
        var last = pressed;
        while (last + 1 < rows.Count && _isMovable(last + 1)) last++;
        if (last == first) return;

        var run = rows.Skip(first).Take(last - first + 1).ToArray();
        var tops = run.Select(row => row.Bounds.Y).ToArray();
        var heights = run.Select(row => row.Bounds.Height).ToArray();
        var from = pressed - first;
        var gap = from + 1 < run.Length
            ? tops[from + 1] - (tops[from] + heights[from])
            : tops[from] - (tops[from - 1] + heights[from - 1]);

        var tile = run[from];
        _drag = new DragState
        {
            First = first,
            From = from,
            Tile = tile,
            StartY = startY,
            Stride = heights[from] + gap,
            Tops = tops,
            Heights = heights,
            Pointer = pointer,
            Target = from,
        };

        pointer.Capture(_items);
        _edgeScrollTimer.Start();
        TopLevel.GetTopLevel(_items)?.AddHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        for (var i = 0; i < run.Length; i++)
        {
            if (i == from) continue;
            run[i].Transitions = SlideTransitions(SlideDuration);
            run[i].RenderTransform = Transform(0, 1);
        }

        // The lifted entry eases up to its raised look; once that is done it must track the pointer without lag.
        tile.ZIndex = 100;
        UnclipAncestors(tile);
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

    // The tops captured in BeginDrag are in the item panel's coordinates, which don't change when the scroll viewer
    // scrolls, so only the pointer has to be re-expressed: its panel Y moves by exactly the distance scrolled.
    private void OnEdgeScrollTick(object? sender, EventArgs e)
    {
        if (_drag is not { } drag || _isSettling) return;

        var step = SpineDragLayout.EdgeScrollStep(drag.PointerViewportY, _scroller.Viewport.Height, EdgeScrollBand, EdgeScrollMaxStep);
        if (step == 0) return;

        var before = _scroller.Offset.Y;
        _scroller.Offset = _scroller.Offset.WithY(before + step);
        var scrolled = _scroller.Offset.Y - before;
        if (scrolled == 0) return;

        UpdateDrag(drag, drag.PointerPanelY + scrolled);
    }

    private void UpdateDrag(DragState drag, double pointerY)
    {
        drag.PointerPanelY = pointerY;
        drag.Offset = SpineDragLayout.ClampOffset(drag.Tops, drag.Heights, drag.From, pointerY - drag.StartY);
        drag.Tile.RenderTransform = Transform(drag.Offset, LiftScale);

        var centre = drag.Tops[drag.From] + drag.Offset + drag.Heights[drag.From] / 2;
        var target = SpineDragLayout.TargetIndex(drag.Tops, drag.Heights, drag.From, centre);
        if (target == drag.Target) return;

        drag.Target = target;
        var rows = Rows;
        for (var i = 0; i < drag.Tops.Length; i++)
        {
            if (i == drag.From) continue;
            rows[drag.First + i].RenderTransform = Transform(SpineDragLayout.NeighbourShift(i, drag.From, target, drag.Stride), 1);
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
                var rows = Rows;
                for (var i = 0; i < drag.Tops.Length; i++)
                {
                    if (i != drag.From) rows[drag.First + i].RenderTransform = Transform(0, 1);
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
            if (commit && target != drag.From) _move(drag.First + drag.From, drag.First + target);
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
        _edgeScrollTimer.Stop();
        TopLevel.GetTopLevel(_items)?.RemoveHandler(InputElement.KeyDownEvent, OnTopLevelKeyDown);

        foreach (var row in Rows)
        {
            row.Transitions = null;
            row.RenderTransform = null;
            row.ZIndex = 0;
            row.Opacity = 1;
            row.Effect = null;
        }

        if (drag?.Pointer.Captured == _items) drag.Pointer.Capture(null);
        RestoreClipping();
    }

    private void UnclipAncestors(Visual tile)
    {
        RestoreClipping();
        for (var visual = tile.GetVisualParent(); visual != null; visual = visual.GetVisualParent())
        {
            _unclipped.Add((visual, visual.ClipToBounds));
            visual.ClipToBounds = false;
            if (ReferenceEquals(visual, _scroller)) break;
        }
    }

    private void RestoreClipping()
    {
        foreach (var (visual, clipToBounds) in _unclipped) visual.ClipToBounds = clipToBounds;
        _unclipped.Clear();
    }

    private static Transitions SlideTransitions(TimeSpan duration) =>
    [
        new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = duration,
            Easing = new CubicEaseOut(),
        },
    ];

    // Every entry always carries the same two operations so a transition can interpolate between any two of them.
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
        Dispatcher.UIThread.Post(action);
    }

    private int IndexOfRowUnder(object? source)
    {
        var rows = Rows;
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (ReferenceEquals(rows[i], visual)) return i;
            }
        }
        return -1;
    }
}
