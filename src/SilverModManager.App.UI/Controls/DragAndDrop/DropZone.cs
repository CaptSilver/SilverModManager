using Avalonia.Controls;
using Avalonia.Input;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>
/// The one place that knows the Avalonia <c>DragEnter</c>/<c>DragOver</c>/<c>Drop</c>/<c>Handled</c> protocol. Makes
/// <c>host</c> and everything inside it a drop surface for <c>target</c>.
/// </summary>
public sealed class DropZone : IDisposable
{
    private readonly Control _host;
    private readonly IDropTarget _target;
    private readonly IErrorPresenter _errors;
    private readonly bool _outer;
    private readonly CancellationTokenSource _disposed = new();
    private IDropFeedback? _feedback;
    private bool _showing;

    // DragOver fires on every mouse move and a verdict can mean opening every dragged archive. Avalonia hands the same
    // data object to every event of one drag while DragPayloads.From mints a new record per call, so the data object is
    // the key. Drop clears it, so the next drag classifies afresh.
    private (WeakReference<IDataObject> Data, DropVerdict Verdict)? _cached;

    /// <param name="host">Becomes the drop surface (AllowDrop is set here and inherited by its children).</param>
    /// <param name="target">What the zone accepts and does.</param>
    /// <param name="errors">Receives a drop that fails; the user sees it there.</param>
    /// <param name="feedback">Where verdicts are shown. Null resolves <see cref="DropFeedback.LayerProperty"/> on the host's top level at the first drag event.</param>
    /// <param name="outer">
    /// The window's zone: handlers also see events an inner zone handled, and then only hide their own feedback and do
    /// nothing else. An inner zone leaves handled events alone.
    /// </param>
    public DropZone(Control host, IDropTarget target, IErrorPresenter errors, IDropFeedback? feedback = null, bool outer = false)
    {
        _host = host;
        _target = target;
        _errors = errors;
        _feedback = feedback;
        _outer = outer;

        // Moving between child controls raises Leave on the old one and Enter (not Over) on the new one, so Enter has
        // to show the feedback too.
        DragDrop.SetAllowDrop(host, true);
        host.AddHandler(DragDrop.DragEnterEvent, OnDragOver, handledEventsToo: outer);
        host.AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: outer);
        host.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, handledEventsToo: outer);
        host.AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: outer);
    }

    public void Dispose()
    {
        _host.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
        _host.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
        _host.RemoveHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _host.RemoveHandler(DragDrop.DropEvent, OnDrop);
        _disposed.Cancel();
        Hide();
    }

    private IDropFeedback? Feedback
        => _feedback ??= TopLevel.GetTopLevel(_host) is { } topLevel ? DropFeedback.GetLayer(topLevel) : null;

    private void Show(DropVerdict verdict)
    {
        _showing = true;
        Feedback?.Show(_host, verdict);
    }

    private void Hide()
    {
        _showing = false;
        Feedback?.Hide();
    }

    private DropVerdict Inspect(IDataObject data)
        => DragPayloads.From(data) is { } payload ? _target.Inspect(payload) : new DropVerdict.Ignore();

    private DropVerdict CachedVerdict(IDataObject data)
    {
        if (_cached is { } cached && cached.Data.TryGetTarget(out var known) && ReferenceEquals(known, data)) return cached.Verdict;
        var verdict = Inspect(data);
        _cached = (new WeakReference<IDataObject>(data), verdict);
        return verdict;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_outer && e.Handled)
        {
            // An inner zone has the drag. Only hide what this zone put up: the inner zone may share the feedback.
            if (_showing) Hide();
            return;
        }

        switch (CachedVerdict(e.Data))
        {
            case DropVerdict.Accept accept:
                e.DragEffects = accept.Effect;
                e.Handled = true;
                Show(accept);
                break;
            case DropVerdict.Refuse refuse:
                e.DragEffects = DragDropEffects.None;
                e.Handled = true;
                Show(refuse);
                break;
            default:
                e.DragEffects = DragDropEffects.None;
                Hide();
                break;
        }
    }

    // A leave between two controls is followed straight away by an enter that shows the feedback again, so hiding here
    // is safe. It can't tell that apart from leaving the window, so the cached verdict stays.
    private void OnDragLeave(object? sender, DragEventArgs e) => Hide();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Hide();
        _cached = null;
        if (_outer && e.Handled) return;

        if (DragPayloads.From(e.Data) is not { } payload) return;

        switch (_target.Inspect(payload))
        {
            case DropVerdict.Accept:
                e.Handled = true;
                _ = DropAsync(payload);
                break;
            case DropVerdict.Refuse:
                // Handled, so an enclosing zone doesn't act on a drag this one understood and turned down.
                e.Handled = true;
                break;
        }
    }

    private async Task DropAsync(DragPayload payload)
    {
        try
        {
            await _target.DropAsync(payload, _disposed.Token);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            // The zone went away mid-drop; nobody is waiting for the result.
        }
        catch (Exception exception)
        {
            await _errors.Report(exception, "handling the drop");
        }
    }
}
