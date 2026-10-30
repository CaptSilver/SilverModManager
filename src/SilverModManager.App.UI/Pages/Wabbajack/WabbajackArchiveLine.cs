using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>
/// The line of one archive in the table. It is built when the row scrolls into view and follows the row afterwards, so
/// a download finishing changes the texts of that line and builds nothing.
/// </summary>
public sealed class WabbajackArchiveLine : Grid
{
    private readonly WabbajackArchiveRow _row;
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _reason = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _install = new() { Content = "Install" };
    private readonly Button _open = new() { Content = "Open page" };
    private IDisposable? _subscription;

    public WabbajackArchiveLine(IWabbajackModlistViewModel viewModel, WabbajackArchiveRow row)
    {
        _row = row;
        Margin = new Thickness(0, 2);
        ColumnDefinitions = new ColumnDefinitions("332,92,132,252,*,Auto,Auto");

        Add(new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0);
        Add(new TextBlock { Text = row.SizeText, VerticalAlignment = VerticalAlignment.Center }, 1);
        Add(new TextBlock { Text = row.Source, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 2);
        Add(_state, 3);
        Add(_reason, 4);
        Add(_install, 5);
        Add(_open, 6);
        _install.Margin = _open.Margin = new Thickness(12, 0, 0, 0);
        _install.Click += (_, _) => viewModel.InstallArchiveCommand.Execute(row).Subscribe();
        _open.Click += (_, _) => viewModel.OpenPageCommand.Execute(row).Subscribe();
        Show();
    }

    private void Add(Control child, int column)
    {
        if (column is >= 0 and <= 3) child.Margin = new Thickness(0, 0, 12, 0);
        SetColumn(child, column);
        Children.Add(child);
    }

    private void Show()
    {
        _state.Text = _row.StateText;
        _reason.Text = _row.Reason;
        ToolTip.SetTip(_reason, _row.Reason == "" ? null : _row.Reason);
        _install.IsVisible = _row.CanInstall;
        _open.IsVisible = _row.CanOpenPage;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A row can change on a download thread; the line is drawn on the UI thread.
        _subscription = _row.Changed.ObserveOn(RxApp.MainThreadScheduler).Subscribe(_ => Show());
        Show();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _subscription?.Dispose();
        _subscription = null;
        base.OnDetachedFromVisualTree(e);
    }

}
