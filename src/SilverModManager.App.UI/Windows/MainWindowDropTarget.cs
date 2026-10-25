using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// Makes <paramref name="host"/> and everything inside it a drop target: shows the overlay naming what the dragged files
/// turn into, and hands dropped files to <paramref name="onDrop"/>.
/// </summary>
public sealed class MainWindowDropTarget
{
    private readonly Control _overlay;
    private readonly TextBlock _overlayText;
    private readonly Func<IReadOnlyList<string>, Task> _onDrop;

    // DragOver fires on every mouse move and classifying opens each file, so the plan is kept for as long as the same
    // files are dragged. It only feeds the overlay: the drop itself classifies afresh.
    private (string[] Paths, MainWindowDropPlan Plan)? _current;

    public MainWindowDropTarget(Control host, Control overlay, TextBlock overlayText, Func<IReadOnlyList<string>, Task> onDrop)
    {
        _overlay = overlay;
        _overlayText = overlayText;
        _onDrop = onDrop;

        // AllowDrop is inherited, so every control inside the host becomes a drop target. Moving between them raises
        // DragLeave on the old one and DragEnter (not DragOver) on the new one, so Enter has to show the overlay too.
        // handledEventsToo: a control that takes its own drops (the modlist page's inbox) marks them handled, and the
        // overlay must not stay up over it.
        DragDrop.SetAllowDrop(host, true);
        host.AddHandler(DragDrop.DragEnterEvent, OnDragOver, handledEventsToo: true);
        host.AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        host.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, handledEventsToo: true);
        host.AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);
    }

    private static string[] DroppedPaths(DragEventArgs e)
        => e.Data.GetFiles()?.Select(static item => item.TryGetLocalPath()).OfType<string>().ToArray() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            _overlay.IsVisible = false;
            return;
        }

        var paths = DroppedPaths(e);
        if (paths.Length == 0)
        {
            _overlay.IsVisible = false;
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var plan = PlanFor(paths);
        e.DragEffects = plan.Items.Any(static item => item.Kind != DropKind.Refused) ? DragDropEffects.Copy : DragDropEffects.None;
        _overlayText.Text = plan.OverlayText;
        _overlay.IsVisible = true;
    }

    private MainWindowDropPlan PlanFor(string[] paths)
    {
        if (_current is { } current && current.Paths.SequenceEqual(paths, StringComparer.Ordinal)) return current.Plan;
        var plan = MainWindowDropPlan.Classify(paths);
        _current = (paths, plan);
        return plan;
    }

    // A leave between two controls is followed straight away by an enter that shows the overlay again, so hiding here
    // is safe. It can't tell that apart from leaving the window, so the plan stays.
    private void OnDragLeave(object? sender, DragEventArgs e) => _overlay.IsVisible = false;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _overlay.IsVisible = false;
        _current = null;
        if (e.Handled) return;

        var paths = DroppedPaths(e);
        if (paths.Length == 0) return;

        e.Handled = true;
        _ = _onDrop(paths);
    }
}
