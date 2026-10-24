using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.FromSoftMerges;

[UsedImplicitly]
public partial class FromSoftMergesViewModel : APageViewModel<IFromSoftMergesViewModel>, IFromSoftMergesViewModel
{
    public const string EmptyStateText = "No file is provided by more than one enabled mod";

    // An apply or a priority change touches many entities in one burst; one refresh after it is enough.
    // The first revision, emitted on subscribe, skips the wait so the page paints at once.
    private static readonly TimeSpan RevisionThrottle = TimeSpan.FromMilliseconds(300);

    private readonly IMergeReports _reports;
    private readonly IConnection _conn;
    private readonly ILogger<FromSoftMergesViewModel> _logger;
    private int _generation;

    [Reactive] public partial LoadoutId LoadoutId { get; set; }
    [Reactive] public partial IReadOnlyList<MergeReportEntry> Entries { get; private set; } = [];
    [Reactive] public partial string Refusal { get; private set; } = "";
    [Reactive] public partial string PriorityOrderHint { get; private set; } = "";
    [Reactive] public partial string Problem { get; private set; } = "";
    [Reactive] public partial string EmptyState { get; private set; } = "";
    [Reactive] public partial bool IsLoading { get; private set; }
    [Reactive] public partial MergeReportEntry? Selected { get; set; }
    [Reactive] public partial IReadOnlyList<string> SelectedNotes { get; private set; } = [];

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public FromSoftMergesViewModel(IWindowManager windowManager, IMergeReports reports, IConnection conn, ILogger<FromSoftMergesViewModel> logger) : base(windowManager)
    {
        _reports = reports;
        _conn = conn;
        _logger = logger;

        TabIcon = IconValues.Swap;
        TabTitle = "Merges";

        RefreshCommand = ReactiveCommand.Create(Refresh);
        this.WhenAnyValue(vm => vm.Selected).Subscribe(selected =>
            SelectedNotes = selected is null ? [] : selected.Notes.Concat(selected.Reason == "" ? [] : [selected.Reason]).ToArray());

        // The first refresh comes from the revision stream: it emits the current revision on subscribe.
        this.WhenActivated(d =>
        {
            this.WhenAnyValue(vm => vm.LoadoutId)
                .Select(id => id == default
                    ? Observable.Empty<Unit>()
                    : LoadoutQueries2.RevisionsWithChildUpdates(_conn, id).ThrottleAfterFirst(RevisionThrottle, TaskPoolScheduler.Default).Select(_ => Unit.Default))
                .Switch()
                .OnUI()
                .Subscribe(_ => Refresh())
                .DisposeWith(d);
        });
    }

    private void Refresh()
    {
        var generation = Interlocked.Increment(ref _generation);
        var id = LoadoutId;
        IsLoading = true;

        // A refresh that was overtaken must not overwrite the newer one's rows.
        Observable.FromAsync<(MergeReport? Report, string Problem)>(async token =>
            {
                try
                {
                    return (Report: await Task.Run(() => _reports.ForLoadout(id, token), token), Problem: "");
                }
                // Nothing cancels the token, so even a cancellation comes from inside the report and is shown, not thrown.
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not build the merge report of loadout {Loadout}", id);
                    return (Report: null, Problem: $"Could not read the merges: {exception.Message}");
                }
            })
            .SubscribeOn(TaskPoolScheduler.Default)
            .OnUI()
            .Subscribe(result =>
            {
                if (generation != Volatile.Read(ref _generation)) return;
                Show(result.Report, result.Problem);
                IsLoading = false;
            });
    }

    private void Show(MergeReport? report, string problem)
    {
        // Read before the rows change: the list view clears the selection as soon as its rows are replaced.
        var selectedPath = Selected?.Path;
        Problem = problem;
        Entries = report?.Entries ?? [];
        Refusal = report?.Refusal ?? "";
        PriorityOrderHint = report?.PriorityOrderHint ?? "";
        EmptyState = report is not null && report.Entries.Count == 0 ? EmptyStateText : "";

        // The same path stays selected across a refresh, as far as it is still listed.
        Selected = Entries.FirstOrDefault(e => e.Path == selectedPath);
    }
}
