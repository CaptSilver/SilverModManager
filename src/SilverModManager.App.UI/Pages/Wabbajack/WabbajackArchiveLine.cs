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
    private readonly Button _signIn = new() { Content = "Sign in" };
    private readonly Button _retry = new() { Content = "Try again" };
    private readonly IObservable<bool> _canRetry;
    private IDisposable? _subscription;
    private IDisposable? _retryEnabled;

    public WabbajackArchiveLine(IWabbajackModlistViewModel viewModel, WabbajackArchiveRow row)
    {
        _row = row;
        Margin = new Thickness(0, 2);
        ColumnDefinitions = new ColumnDefinitions("332,92,132,252,*,Auto,Auto,Auto,Auto");
        // The note gets a row of its own under the line, across all of it: beside the buttons it had a sliver to wrap in.
        RowDefinitions = new RowDefinitions("Auto,Auto");

        Add(new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0);
        Add(new TextBlock { Text = row.SizeText, VerticalAlignment = VerticalAlignment.Center }, 1);
        Add(new TextBlock { Text = row.Source, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 2);
        Add(_state, 3);
        Add(_install, 5);
        Add(_signIn, 6);
        Add(_open, 7);
        Add(_retry, 8);
        SetRow(_reason, 1);
        SetColumnSpan(_reason, ColumnDefinitions.Count);
        Children.Add(_reason);
        _install.Margin = _open.Margin = _signIn.Margin = _retry.Margin = new Thickness(12, 0, 0, 0);
        _install.Click += (_, _) => viewModel.InstallArchiveCommand.Execute(row).Subscribe();
        _open.Click += (_, _) => viewModel.OpenPageCommand.Execute(row).Subscribe();
        _signIn.Click += (_, _) => viewModel.SignInToSourceCommand.Execute(row).Subscribe();
        _retry.Click += (_, _) => viewModel.RetryArchiveCommand.Execute(row).Subscribe();
        _canRetry = viewModel.RetryArchiveCommand.CanExecute;
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
        _signIn.IsVisible = _row.CanSignIn;
        _open.IsVisible = _row.CanOpenPage;
        _retry.IsVisible = _row.CanRetry;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A row can change on a download thread; the line is drawn on the UI thread.
        _subscription = _row.Changed.ObserveOn(RxApp.MainThreadScheduler).Subscribe(_ => Show());
        // Off while downloads run, like the toolbar's Download.
        _retryEnabled = _canRetry.ObserveOn(RxApp.MainThreadScheduler).Subscribe(can => _retry.IsEnabled = can);
        Show();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _subscription?.Dispose();
        _subscription = null;
        _retryEnabled?.Dispose();
        _retryEnabled = null;
        base.OnDetachedFromVisualTree(e);
    }

}
