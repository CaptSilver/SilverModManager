using System.Reactive.Disposables;
using System.Reactive.Linq;
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
using SilverModManager.App.UI.Controls.Spine;

namespace SilverModManager.App.UI.Pages.MyGames;

public partial class MyGamesView : ReactiveUserControl<IMyGamesViewModel>
{
    private const double LiftScale = 1.05;
    private const double LiftedOpacity = 0.92;
    private static readonly TimeSpan DefaultSlideDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DefaultLiftDuration = TimeSpan.FromMilliseconds(120);
    private const double EdgeScrollBand = 64;
    private const double EdgeScrollMaxStep = 18;
    private static readonly TimeSpan EdgeScrollInterval = TimeSpan.FromMilliseconds(16);

    private Point? _pressPoint;
    private int _pressedIndex = -1;
    private DragState? _drag;
    private bool _isSettling;
    private readonly Avalonia.Threading.DispatcherTimer _edgeScrollTimer;

    // The panels around the grid clip to their bounds, which cut the lifted tile's scaled edges and shadow off;
    // clipping is switched off on that chain while a tile is lifted and put back afterwards.
    private readonly List<(Visual Visual, bool ClipToBounds)> _unclipped = [];

    /// <summary>Layout snapshot and live position of the tile being dragged.</summary>
    private sealed class DragState
    {
        public required int From { get; init; }
        public required Control Tile { get; init; }
        public required Point Start { get; init; }
        public required Rect[] Slots { get; init; }
        public required IPointer Pointer { get; init; }
        public int Target { get; set; }

        // Pointer in the tile panel's coordinates (moves with the content when the page scrolls) and its Y in the
        // scroll viewer's (stays put); the timer needs both to scroll and then re-place the tile.
        public Point PointerPanel { get; set; }
        public double PointerViewportY { get; set; }
    }

    public MyGamesView()
    {
        InitializeComponent();

        _edgeScrollTimer = new Avalonia.Threading.DispatcherTimer { Interval = EdgeScrollInterval };
        _edgeScrollTimer.Tick += OnEdgeScrollTick;

        this.WhenActivated(d =>
            {
                this.WhenAnyValue(view => view.ViewModel!.InstalledGames)
                    .BindToView(this, view => view.DetectedGamesItemsControl.ItemsSource)
                    .DisposeWith(d);

                this.WhenAnyValue(view => view.ViewModel!.SupportedGames)
                    .BindToView(this, view => view.SupportedGamesItemsControl.ItemsSource)
                    .DisposeWith(d);

                this.BindCommand(ViewModel, vm => vm.AddGameFolderCommand, view => view.AddGameFolderButton)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.LocatorProblemsNotice, view => view.LocatorProblemsNotice.Text)
                    .DisposeWith(d);

                this.OneWayBind(ViewModel, vm => vm.LocatorProblemsNotice, view => view.LocatorProblemsNotice.IsVisible, notice => !string.IsNullOrEmpty(notice))
                    .DisposeWith(d);

                this.WhenAnyValue(view  => view.ViewModel!.InstalledGames.Count)
                    .Select(installedCount  => installedCount == 0)
                    .Subscribe(isEmpty =>
                        {
                            DetectedGamesEmptyState.IsActive = isEmpty;
                        }
                    )
                    .DisposeWith(d);

                // The tiles hold buttons that mark their pointer events handled, so the press has to be watched on the
                // way down. A click without movement never starts a drag. Pointer capture rather than DragDrop: the
                // tile itself follows the pointer instead of the OS drag cursor.
                var tiles = DetectedGamesItemsControl;
                tiles.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
                tiles.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
                tiles.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
                tiles.PointerCaptureLost += OnPointerCaptureLost;
                tiles.ItemsView.CollectionChanged += OnTilesChanged;

                Disposable.Create(() =>
                {
                    tiles.RemoveHandler(PointerPressedEvent, OnPointerPressed);
                    tiles.RemoveHandler(PointerMovedEvent, OnPointerMoved);
                    tiles.RemoveHandler(PointerReleasedEvent, OnPointerReleased);
                    tiles.PointerCaptureLost -= OnPointerCaptureLost;
                    tiles.ItemsView.CollectionChanged -= OnTilesChanged;
                    EndDrag();
                }).DisposeWith(d);
            }
        );
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isSettling || _drag != null) return;
        if (!e.GetCurrentPoint(DetectedGamesItemsControl).Properties.IsLeftButtonPressed) return;
        _pressedIndex = IndexOfTileUnder(e.Source);
        _pressPoint = _pressedIndex < 0 ? null : e.GetPosition(DetectedGamesItemsControl.ItemsPanelRoot);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is { } drag)
        {
            drag.PointerViewportY = e.GetPosition(PageScroller).Y;
            UpdateDrag(drag, e.GetPosition(DetectedGamesItemsControl.ItemsPanelRoot));
            e.Handled = true;
            return;
        }

        if (_pressPoint is not { } start || DetectedGamesItemsControl.ItemsPanelRoot is not { } panel) return;
        var delta = e.GetPosition(panel) - start;
        if (!SpineDragLayout.ExceedsThreshold(delta.X, delta.Y)) return;

        _pressPoint = null;
        BeginDrag(_pressedIndex, start, e.Pointer);
        if (_drag is { } started)
        {
            started.PointerViewportY = e.GetPosition(PageScroller).Y;
            UpdateDrag(started, e.GetPosition(panel));
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressPoint = null;
        if (_drag is not { } drag) return;

        e.Handled = true;
        _ = SettleAsync(drag, commit: true);
    }

    // Losing the capture (window deactivated, popup opened) must not leave a tile stuck in the air.
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag is { } drag && !_isSettling) _ = SettleAsync(drag, commit: false);
    }

    // A game added or removed mid-drag invalidates the indices and positions the drag captured,
    // so the drag is dropped rather than committing a move against the wrong tiles.
    private void OnTilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_drag != null) EndDrag();
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is not { } drag || _isSettling) return;
        e.Handled = true;
        _ = SettleAsync(drag, commit: false);
    }

    private void BeginDrag(int from, Point start, IPointer pointer)
    {
        var tiles = Tiles;
        if (tiles.Count < 2 || from < 0 || from >= tiles.Count) return;

        var tile = tiles[from];
        _drag = new DragState
        {
            From = from,
            Tile = tile,
            Start = start,
            Slots = tiles.Select(t => t.Bounds).ToArray(),
            Pointer = pointer,
            Target = from,
        };

        pointer.Capture(DetectedGamesItemsControl);
        _edgeScrollTimer.Start();
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        for (var i = 0; i < tiles.Count; i++)
        {
            if (i == from) continue;
            tiles[i].Transitions = SlideTransitions(SlideDuration);
            tiles[i].RenderTransform = Transform(default, 1);
        }

        // The lifted tile eases up to its raised look; once that is done it must track the pointer without lag.
        tile.ZIndex = 100;
        UnclipAncestors(tile);
        tile.Opacity = LiftedOpacity;
        tile.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 3, BlurRadius = 10, Color = Colors.Black, Opacity = 0.45 };
        tile.Transitions = SlideTransitions(LiftDuration);
        tile.RenderTransform = Transform(default, LiftScale);
        var state = _drag;
        DelayThen(LiftDuration, () =>
        {
            if (_drag != state || state is null) return;
            state.Tile.Transitions = null;
        });
    }

    // The slots captured in BeginDrag are in the tile panel's coordinates, which don't change when the page scrolls,
    // so only the pointer has to be re-expressed: its panel Y moves by exactly the distance scrolled.
    private void OnEdgeScrollTick(object? sender, EventArgs e)
    {
        if (_drag is not { } drag || _isSettling) return;

        var step = SpineDragLayout.EdgeScrollStep(drag.PointerViewportY, PageScroller.Viewport.Height, EdgeScrollBand, EdgeScrollMaxStep);
        if (step == 0) return;

        var before = PageScroller.Offset.Y;
        PageScroller.Offset = PageScroller.Offset.WithY(before + step);
        var scrolled = PageScroller.Offset.Y - before;
        if (scrolled == 0) return;

        UpdateDrag(drag, drag.PointerPanel + new Vector(0, scrolled));
    }

    private void UpdateDrag(DragState drag, Point pointer)
    {
        drag.PointerPanel = pointer;
        var offset = GameTileDragLayout.ClampOffset(drag.Slots, drag.From, pointer - drag.Start);
        drag.Tile.RenderTransform = Transform(offset, LiftScale);

        var target = GameTileDragLayout.TargetIndex(drag.Slots, drag.From, drag.Slots[drag.From].Translate(offset));
        if (target == drag.Target) return;

        drag.Target = target;
        var tiles = Tiles;
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i == drag.From) continue;
            tiles[i].RenderTransform = Transform(GameTileDragLayout.NeighbourShift(drag.Slots, i, drag.From, target), 1);
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
                var tiles = Tiles;
                for (var i = 0; i < tiles.Count; i++)
                {
                    if (i != drag.From) tiles[i].RenderTransform = Transform(default, 1);
                }
            }

            drag.Tile.Transitions = SlideTransitions(SlideDuration);
            drag.Tile.Opacity = 1;
            drag.Tile.Effect = null;
            drag.Tile.RenderTransform = Transform(GameTileDragLayout.SettleOffset(drag.Slots, drag.From, target), 1);

            await Task.Delay(SlideDuration);
            if (_drag != drag) return;

            // Drop the transforms first so the new order is not painted with them still applied.
            EndDrag();
            if (commit && target != drag.From) ViewModel?.MoveInstalledGame(drag.From, target);
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
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);

        foreach (var tile in Tiles)
        {
            tile.Transitions = null;
            tile.RenderTransform = null;
            tile.ZIndex = 0;
            tile.Opacity = 1;
            tile.Effect = null;
        }

        if (drag?.Pointer.Captured == DetectedGamesItemsControl) drag.Pointer.Capture(null);
        RestoreClipping();
    }

    private void UnclipAncestors(Visual tile)
    {
        RestoreClipping();
        for (var visual = tile.GetVisualParent(); visual != null; visual = visual.GetVisualParent())
        {
            _unclipped.Add((visual, visual.ClipToBounds));
            visual.ClipToBounds = false;
            if (ReferenceEquals(visual, PageScroller)) break;
        }
    }

    private void RestoreClipping()
    {
        foreach (var (visual, clipToBounds) in _unclipped) visual.ClipToBounds = clipToBounds;
        _unclipped.Clear();
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
    private static ITransform Transform(Vector offset, double scale)
    {
        var builder = TransformOperations.CreateBuilder(2);
        builder.AppendTranslate(offset.X, offset.Y);
        builder.AppendScale(scale, scale);
        return builder.Build();
    }

    private static async void DelayThen(TimeSpan delay, Action action)
    {
        await Task.Delay(delay);
        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    private IReadOnlyList<Control> Tiles => DetectedGamesItemsControl.ItemsPanelRoot?.Children ?? [];

    private int IndexOfTileUnder(object? source)
    {
        var tiles = Tiles;
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            var index = tiles.ToList().FindIndex(tile => ReferenceEquals(tile, visual));
            if (index >= 0) return index;
        }
        return -1;
    }
}
