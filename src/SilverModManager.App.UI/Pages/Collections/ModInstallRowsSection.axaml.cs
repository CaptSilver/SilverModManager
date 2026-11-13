using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>
/// The Mods section of an install page: a header, a filter and one <see cref="ModInstallLine"/> per mod folder. It
/// is shared by every page that shows an install's rows. It follows <see cref="Rows"/> only while it is on screen,
/// so a page that is closed while the install runs leaves nothing subscribed to the job.
/// </summary>
public partial class ModInstallRowsSection : UserControl
{
    public static readonly StyledProperty<IModInstallRows?> RowsProperty = AvaloniaProperty.Register<ModInstallRowsSection, IModInstallRows?>(nameof(Rows));
    public static readonly StyledProperty<ModInstallFilter> FilterProperty = AvaloniaProperty.Register<ModInstallRowsSection, ModInstallFilter>(nameof(Filter));

    private readonly ObservableCollection<ModInstallRow> _shown = [];
    private IReadOnlyList<ModInstallRow> _all = [];
    private IDisposable? _subscription;

    public ModInstallRowsSection()
    {
        InitializeComponent();
        ModRows.ItemTemplate = new FuncDataTemplate<ModInstallRow>((row, _) => row is null ? null : new ModInstallLine(row, Scheduler));
        ModRows.ItemsSource = _shown;
        ModsFilterBox.SelectionChanged += (_, _) =>
        {
            if (ModsFilterBox.SelectedIndex >= 0) Filter = (ModInstallFilter)ModsFilterBox.SelectedIndex;
        };
    }

    public IModInstallRows? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public ModInstallFilter Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    /// <summary>Where the lines and the list are updated; a test hands in its own so it decides when that happens.</summary>
    public IScheduler Scheduler { get; init; } = RxApp.MainThreadScheduler;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty) Follow();
        else if (change.Property == FilterProperty)
        {
            ModsFilterBox.SelectedIndex = (int)Filter;
            Refilter();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unfollow();
        base.OnDetachedFromVisualTree(e);
    }

    private void Unfollow()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void Follow()
    {
        Unfollow();
        _all = [];
        _shown.Clear();
        ModsHeader.Text = "";
        IsVisible = false;
        if (Rows is not { } rows || !this.IsAttachedToVisualTree()) return;

        var subscriptions = new CompositeDisposable();
        // One add burst per feed, so a plain ObserveOn is enough here; the totals change with every folder and take the latest.
        rows.Connect()
            .ObserveOn(Scheduler)
            .SortAndBind(out var all, SortExpressionComparer<ModInstallRow>.Ascending(row => row.Index))
            .Subscribe(_ => Refilter())
            .DisposeWith(subscriptions);
        _all = all;
        rows.Totals
            .ObserveLatestOn(Scheduler)
            .Subscribe(totals =>
            {
                ModsHeader.Text = ModInstallText.Header(totals);
                Refilter();
            })
            .DisposeWith(subscriptions);
        _subscription = subscriptions;
    }

    // A row changing state does not touch the list, so the filter is reapplied when the totals move.
    private void Refilter()
    {
        // The section itself hides, so the margin or spacing its host gives it goes too.
        IsVisible = _all.Count > 0;
        var wanted = _all.Where(row => ModInstallText.Matches(Filter, row.Current)).ToList();
        ListReconcile.Reconcile(_shown, wanted);
    }
}
