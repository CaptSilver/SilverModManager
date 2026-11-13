using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// The line of one mod folder in the Mods section. It is built when the row scrolls into view and follows the row
/// while it is on screen, so a folder changing state changes the texts of that line and builds nothing. The row
/// reports on the install's threads, once per file, so the line takes only the latest state and only at the pace
/// the UI thread asks for it.
/// </summary>
public sealed class ModInstallLine : Grid
{
    private static int _built;

    private readonly ModInstallRow _row;
    private readonly IScheduler _scheduler;
    private readonly TextBlock _state = new() { Name = "ModState", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { Name = "ModSummary", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private IDisposable? _subscription;

    /// <summary>Lines built so far in this process, for a test that checks only the lines in view are made.</summary>
    public static int Built => Volatile.Read(ref _built);

    public ModInstallLine(ModInstallRow row, IScheduler scheduler)
    {
        _row = row;
        _scheduler = scheduler;
        Interlocked.Increment(ref _built);
        Margin = new Thickness(0, 2);
        ColumnDefinitions = new ColumnDefinitions("3*,120,4*");

        var name = new TextBlock { Text = row.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        ToolTip.SetTip(name, row.Name);
        _state.Margin = new Thickness(0, 0, 12, 0);
        SetColumn(_state, 1);
        SetColumn(_summary, 2);
        Children.Add(name);
        Children.Add(_state);
        Children.Add(_summary);
        Show();
    }

    private void Show()
    {
        var current = _row.Current;
        _state.Text = ModInstallText.State(current.State);
        _summary.Text = ModInstallText.Summary(current);
        ToolTip.SetTip(_summary, current.State == ModInstallState.Failed ? current.Reason : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _subscription = _row.Changed.ObserveLatestOn(_scheduler).Subscribe(_ => Show());
        // A recycled line may have missed changes while it was off screen.
        Show();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _subscription?.Dispose();
        _subscription = null;
        base.OnDetachedFromVisualTree(e);
    }
}
