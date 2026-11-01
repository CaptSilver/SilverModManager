using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SilverModManager.App.UI.X11.TestHost;

/// <summary>
/// A window at a fixed position with one border that allows drops. It reports what each drag event carries and answers
/// Copy to a drag that has files, so the receiver's reply (Status, Finished) reflects a window that wants them.
/// </summary>
public sealed class DragProbeWindow : Window
{
    private readonly HostReport _report;
    private readonly Border _target;

    public DragProbeWindow(HostReport report)
    {
        _report = report;
        _target = new Border
        {
            Margin = new Thickness(50),
            // Hit testing skips controls without a background.
            Background = Brushes.SlateGray,
        };
        DragDrop.SetAllowDrop(_target, true);
        _target.AddHandler(DragDrop.DragEnterEvent, (_, e) => Answer("DragEnter", e));
        _target.AddHandler(DragDrop.DragOverEvent, (_, e) => Answer("DragOver", e));
        _target.AddHandler(DragDrop.DragLeaveEvent, (_, e) => Answer("DragLeave", e));
        _target.AddHandler(DragDrop.DropEvent, (_, e) => Answer("Drop", e));

        Width = 400;
        Height = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(100, 100);
        Content = _target;

        // Layout has run by Background priority, so the border has a size and the window a mapped position.
        Opened += (_, _) => Dispatcher.UIThread.Post(ReportReady, DispatcherPriority.Background);
    }

    private void ReportReady()
    {
        var inWindow = new Point(_target.Bounds.Width / 2, _target.Bounds.Height / 2);
        var root = this.PointToScreen(_target.TranslatePoint(inWindow, this) ?? inWindow);
        _report.Ready(TryGetPlatformHandle()?.Handle ?? 0, root.X, root.Y);
    }

    private void Answer(string kind, DragEventArgs e)
    {
        var paths = PathsOf(e);
        _report.Drag(kind, paths);
        e.DragEffects = paths.Count > 0 ? e.DragEffects & DragDropEffects.Copy : DragDropEffects.None;
    }

#pragma warning disable CS0618 // IDataObject is what the receiver hands the window.
    private static List<string> PathsOf(DragEventArgs e) =>
        e.Data.Get(DataFormats.Files) is IEnumerable<IStorageItem> items
            ? items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList()
            : [];
#pragma warning restore CS0618
}
