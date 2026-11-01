using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SilverModManager.App.UI.Controls.Spine;

namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>
/// Starts a <see cref="DragDrop"/> from the rows of a <see cref="Avalonia.Controls.TreeDataGrid"/> carrying a
/// <see cref="DragPayload"/>. The grid's own <c>AutoDragDropRows</c> is for reordering inside the grid; this is the
/// starter for dragging rows to somewhere else.
/// </summary>
/// <remarks>
/// The rows are decided at the drag threshold, not on the press. The grid selects an unselected row on press, in a
/// bubbling handler that runs after ours, and leaves an already selected row alone until the release, so by the
/// threshold the selection is either the freshly pressed row or the whole multi-selection. The in-process drag source
/// swallows the release that ends the drag, so the grid never sees it and the multi-selection survives the drop.
/// Pressed on the way down because row content marks its pointer events handled.
/// </remarks>
/// <typeparam name="TModel">The row model type, as <see cref="TreeDataGridRow.Model"/> holds it.</typeparam>
public sealed class TreeDataGridDragSource<TModel> : IDisposable where TModel : class
{
    private readonly Avalonia.Controls.TreeDataGrid _grid;
    private readonly Func<IReadOnlyList<TModel>> _selectedModels;
    private readonly Func<IReadOnlyList<TModel>, DragPayload?> _payloadFor;
    private readonly DragDropEffects _allowedEffects;

    private TModel? _pressed;
    private Point _pressPoint;
    private bool _isDragging;

    /// <param name="grid">The grid whose rows are dragged.</param>
    /// <param name="selectedModels">The grid's current selection.</param>
    /// <param name="payloadFor">Builds the payload for the rows about to be dragged; null means nothing to drag.</param>
    /// <param name="allowedEffects">What the drop may do with the rows.</param>
    public TreeDataGridDragSource(
        Avalonia.Controls.TreeDataGrid grid,
        Func<IReadOnlyList<TModel>> selectedModels,
        Func<IReadOnlyList<TModel>, DragPayload?> payloadFor,
        DragDropEffects allowedEffects)
    {
        _grid = grid;
        _selectedModels = selectedModels;
        _payloadFor = payloadFor;
        _allowedEffects = allowedEffects;

        grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public void Dispose()
    {
        _grid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _grid.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        _grid.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        _pressed = null;
    }

    /// <summary>
    /// What a drag that starts on <paramref name="pressed"/> carries: the selection when the pressed row is part of
    /// it, otherwise the pressed row alone.
    /// </summary>
    public static IReadOnlyList<TModel> RowsToDrag(TModel pressed, IReadOnlyList<TModel> selection)
        => selection.Contains(pressed) ? selection : [pressed];

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        if (_isDragging || e.GetCurrentPoint(_grid).Properties.IsLeftButtonPressed is false) return;

        if (RowUnder(e.Source) is not { } row) return;
        _pressed = row;
        _pressPoint = e.GetPosition(_grid);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed) return;

        var delta = e.GetPosition(_grid) - _pressPoint;
        if (SpineDragLayout.ExceedsThreshold(delta.X, delta.Y) is false) return;

        _pressed = null;
        var rows = RowsToDrag(pressed, _selectedModels());
        if (_payloadFor(rows) is not { } payload) return;

        e.Handled = true;
        _ = DragAsync(e, payload);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _pressed = null;

    private async Task DragAsync(PointerEventArgs trigger, DragPayload payload)
    {
        _isDragging = true;
        try
        {
            await DragDrop.DoDragDrop(trigger, DragPayloads.ToDataObject(payload), _allowedEffects);
        }
        finally
        {
            _isDragging = false;
        }
    }

    private static TModel? RowUnder(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TreeDataGridRow row) return row.Model as TModel;
        }
        return null;
    }
}
