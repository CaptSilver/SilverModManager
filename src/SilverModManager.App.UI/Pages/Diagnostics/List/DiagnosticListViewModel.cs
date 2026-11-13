using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using DynamicData;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Controls.Diagnostics;
using SilverModManager.Platform.Proton.Support;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Reloaded.Memory.Extensions;
using ObservableExtensions = R3.ObservableExtensions;

namespace SilverModManager.App.UI.Pages.Diagnostics;

[UsedImplicitly]
internal partial class DiagnosticListViewModel : APageViewModel<IDiagnosticListViewModel>, IDiagnosticListViewModel
{
    [Reactive] private partial Diagnostic[] Diagnostics { get; set; } = Array.Empty<Diagnostic>();

    [Reactive] public partial IDiagnosticEntryViewModel[] DiagnosticEntries { get; private set; } = Array.Empty<IDiagnosticEntryViewModel>();

    [Reactive] public partial LoadoutId LoadoutId { get; set; }

    [Reactive] public partial int NumCritical { get; private set; }
    [Reactive] public partial int NumWarnings { get; private set; }
    [Reactive] public partial int NumSuggestions { get; private set; }

    [Reactive] public partial DiagnosticFilter Filter { get; set; }

    [Reactive] public partial string SupportBundleMessage { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> CopySupportBundleCommand { get; }

    [Reactive] private partial DiagnosticClearAction[] ClearActions { get; set; } = [];
    private int _clearsRun;
    [Reactive] public partial bool CanClear { get; private set; }
    [Reactive] public partial string ClearLabel { get; private set; } = "";
    [Reactive] public partial string ClearMessage { get; private set; } = "";
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    [Reactive] public partial IReadOnlyList<IgnoredFinding> IgnoredFindings { get; private set; } = [];
    public ReactiveCommand<EntityId, Unit> ShowAgainCommand { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private readonly ObservableAsPropertyHelper<bool> _isRefreshing;
    public bool IsRefreshing => _isRefreshing.Value;

    /// <summary>
    /// How long window activations have to stay quiet before the diagnostics are refreshed; alt-tabbing
    /// through the window produces several in a row.
    /// </summary>
    private static readonly TimeSpan ActivationDebounce = TimeSpan.FromMilliseconds(500);


    private const DiagnosticFilter AllFilter = DiagnosticFilter.Critical | DiagnosticFilter.Warnings | DiagnosticFilter.Suggestions;

    [Reactive] private partial DiagnosticSettings Settings { get; set; }

    public DiagnosticListViewModel(
        IServiceProvider serviceProvider,
        IWindowManager windowManager,
        IConnection connection,
        IDiagnosticManager diagnosticManager,
        IDiagnosticWriter diagnosticWriter,
        ISettingsManager settingsManager,
        ISupportBundleWriter supportBundleWriter,
        IAvaloniaInterop interop) : base(windowManager)
    {
        // The manager joins a refresh that is already running, so a second request is harmless.
        RefreshCommand = ReactiveCommand.CreateFromTask(() => diagnosticManager.RefreshAsync(LoadoutId));
        _isRefreshing = RefreshCommand.IsExecuting.ToProperty(this, vm => vm.IsRefreshing);

        CopySupportBundleCommand = ReactiveCommand.CreateFromTask(() => CopySupportBundle(supportBundleWriter, interop));
        ClearCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Interlocked.Increment(ref _clearsRun);
            ClearMessage = await Clear(ClearActions);
            ClearActions = await Task.Run(() => diagnosticManager.GetClearActions(LoadoutId));
            RefreshCommand.Execute().SubscribeWithErrorLogging();
        });

        ShowAgainCommand = ReactiveCommand.CreateFromTask<EntityId>(async row =>
        {
            using var tx = connection.BeginTransaction();
            tx.Delete(DiagnosticDismissal.Load(connection.Db, row), recursive: false);
            await tx.Commit();
            await diagnosticManager.RefreshAsync(LoadoutId);
        });

        TabIcon = IconValues.Cardiology;
        TabTitle = Language.DiagnosticListViewModel_DiagnosticListViewModel_Diagnostics;

        Settings = settingsManager.Get<DiagnosticSettings>();
        ObservableExtensions.AsSystemObservable(settingsManager.GetChanges<DiagnosticSettings>(prependCurrent: false)).BindToVM(this, vm => vm.Settings);

        this.WhenActivated(disposable =>
        {
            Filter = AllFilter;

            var serialDisposable = new SerialDisposable();
            serialDisposable.DisposeWith(disposable);

            // The ignored list follows the rows themselves: ignoring the last finding leaves the diagnostics empty, so
            // they cannot be what announces the change.
            this.WhenAnyValue(vm => vm.LoadoutId)
                .Select(loadoutId => connection.ObserveDatoms(DiagnosticDismissal.Loadout, loadoutId)
                    .AsEntityIds()
                    .Transform(datom => DiagnosticDismissal.Load(connection.Db, datom.E))
                    .QueryWhenChanged(rows => (IReadOnlyList<IgnoredFinding>)rows.Items
                        .Where(row => row.IsValid())
                        .Select(row => new IgnoredFinding(row.Title, row.Subject, row.GetCreatedAt(), row.Id))
                        .OrderBy(finding => finding.When).ThenBy(finding => finding.Row.Value)
                        .ToArray()))
                .Switch()
                .OnUI()
                .BindToVM(this, vm => vm.IgnoredFindings)
                .DisposeWith(disposable);

            // get diagnostics from the manager
            this.WhenAnyValue(vm => vm.LoadoutId)
                .Do(loadoutId =>
                {
                    serialDisposable.Disposable = diagnosticManager
                        .GetLoadoutDiagnostics(loadoutId)
                        .OnUI()
                        .BindToVM(this, vm => vm.Diagnostics);

                    // Whatever the diagnostics read outside the database may have changed since they last ran.
                    RefreshCommand.Execute().SubscribeWithErrorLogging();
                })
                .SubscribeWithErrorLogging()
                .DisposeWith(disposable);

            // A fix made in another program shows up when the user comes back. The window manager raises
            // ActiveWindow changes without changing the value in the usual one-window case, so the window's
            // own IsActive is what reports the return.
            var activeWindow = windowManager.WhenAnyValue(manager => manager.ActiveWindow);
            var switchedWindow = activeWindow.Skip(1).Select(_ => System.Reactive.Unit.Default);
            var regainedFocus = activeWindow
                .Select(window => window.WhenAnyValue(w => w.IsActive).Skip(1))
                .Switch()
                .Where(isActive => isActive)
                .Select(_ => System.Reactive.Unit.Default);

            switchedWindow.Merge(regainedFocus)
                .Throttle(ActivationDebounce)
                .Subscribe(_ => RefreshCommand.Execute().SubscribeWithErrorLogging())
                .DisposeWith(disposable);

            // What can be cleared is read from disk again after every run, since a run is what notices new files.
            this.WhenAnyValue(vm => vm.Diagnostics, vm => vm.LoadoutId)
                .Select(tuple => tuple.Item2)
                .ObserveOn(RxApp.TaskpoolScheduler)
                .Select(loadoutId => (ClearsRun: Volatile.Read(ref _clearsRun), Actions: diagnosticManager.GetClearActions(loadoutId)))
                .OnUI()
                // A look that started before a clear read the folder before it was emptied.
                .Where(look => look.ClearsRun == Volatile.Read(ref _clearsRun))
                .Select(look => look.Actions)
                .BindToVM(this, vm => vm.ClearActions)
                .DisposeWith(disposable);

            this.WhenAnyValue(vm => vm.ClearActions)
                .Subscribe(actions =>
                {
                    CanClear = actions.Length > 0;
                    ClearLabel = string.Join(", ", actions.Select(action => action.Label));
                })
                .DisposeWith(disposable);

            // filter diagnostics
            var filteredDiagnostics = this.WhenAnyValue(
                    vm => vm.Diagnostics,
                    vm => vm.Filter,
                    vm => vm.Settings)
                .Select(tuple =>
                {
                    var (diagnostics, filter, settings) = tuple;

                    return diagnostics
                        .Where(diagnostic => filter.HasFlagFast(SeverityToFilter(diagnostic.Severity)))
                        .Where(diagnostic => diagnostic.Severity >= settings.MinimumSeverity)
                        .ToArray();
                })
                .Replay(bufferSize: 1);

            filteredDiagnostics.Connect().DisposeWith(disposable);

            // diagnostics to entries
            filteredDiagnostics
                // Formatting a summary can read the database, so the entries are built off the UI thread and only the finished array is marshalled.
                .ObserveOn(RxApp.TaskpoolScheduler)
                .Select(diagnostics => diagnostics
                    .Select(diagnostic => (IDiagnosticEntryViewModel)new DiagnosticEntryViewModel(diagnostic, diagnosticWriter))
                    .ToArray()
                )
                .OnUI()
                .BindToVM(this, vm => vm.DiagnosticEntries)
                .DisposeWith(disposable);

            // Counts follow the minimum severity as well as the diagnostics, so the tab headers match the list.
            var severityCountObservable = this.WhenAnyValue(vm => vm.Diagnostics, vm => vm.Settings)
                .Select(tuple => tuple.Item1
                    .Select(diagnostic => diagnostic.Severity)
                    .Where(severity => severity >= tuple.Item2.MinimumSeverity)
                    .GroupBy(x => x)
                    .ToDictionary(group => group.Key, group => group.Count())
                )
                .Replay(bufferSize: 1);

            severityCountObservable.Connect().DisposeWith(disposable);

            // diagnostic counts
            severityCountObservable
                .Select(dict => dict.GetValueOrDefault(DiagnosticSeverity.Suggestion, defaultValue: 0))
                .BindToVM(this, vm => vm.NumSuggestions)
                .DisposeWith(disposable);

            severityCountObservable
                .Select(dict => dict.GetValueOrDefault(DiagnosticSeverity.Warning, defaultValue: 0))
                .BindToVM(this, vm => vm.NumWarnings)
                .DisposeWith(disposable);

            severityCountObservable
                .Select(dict => dict.GetValueOrDefault(DiagnosticSeverity.Critical, defaultValue: 0))
                .BindToVM(this, vm => vm.NumCritical)
                .DisposeWith(disposable);

            var entriesSerialDisposable = new SerialDisposable();
            entriesSerialDisposable.DisposeWith(disposable);

            // see details command
            this.WhenAnyValue(vm => vm.DiagnosticEntries)
                .SubscribeWithErrorLogging(entries =>
                {
                    entriesSerialDisposable.Disposable = null;
                    var compositeDisposable = new CompositeDisposable();

                    foreach (var entry in entries)
                    {
                        entry
                            .WhenAnyObservable(x => x.SeeDetailsCommand)
                            .SubscribeWithErrorLogging(tuple =>
                            {
                                var (diagnostic, info) = tuple;
                                var workspaceController = GetWorkspaceController();

                                var pageData = new PageData
                                {
                                    FactoryId = DiagnosticDetailsPageFactory.StaticId,
                                    Context = new DiagnosticDetailsPageContext
                                    {
                                        Diagnostic = diagnostic,
                                        LoadoutId = LoadoutId,
                                    },
                                };

                                var behavior = workspaceController.GetOpenPageBehavior(pageData, info);
                                workspaceController.OpenPage(WorkspaceId, pageData, behavior);
                            })
                            .DisposeWith(compositeDisposable);
                    }

                    entriesSerialDisposable.Disposable = compositeDisposable;
                })
                .DisposeWith(disposable);
        });
    }

    private async Task CopySupportBundle(ISupportBundleWriter writer, IAvaloniaInterop interop)
    {
        SupportBundleMessage = "Collecting logs and launch state...";

        string path;
        try
        {
            path = (await writer.WriteAsync()).ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SupportBundleMessage = $"Could not write the support bundle: {ex.Message}";
            return;
        }

        try
        {
            await interop.SetClipboardTextAsync(path);
            SupportBundleMessage = $"Support bundle written. Its path is on the clipboard: {path}";
        }
        catch (InvalidOperationException)
        {
            SupportBundleMessage = $"Support bundle written to {path}. Could not copy the path to the clipboard.";
        }
    }

    // A failure is shown on the page: clearing is the user's own request, so an error here is not a crash.
    private static async Task<string> Clear(DiagnosticClearAction[] actions)
    {
        var results = new List<string>();
        foreach (var action in actions)
        {
            try
            {
                results.Add(await action.ClearAsync(CancellationToken.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                results.Add($"Could not clear: {ex.Message}");
            }
        }
        return string.Join(" ", results);
    }

    private static DiagnosticFilter SeverityToFilter(DiagnosticSeverity severity)
    {
        return severity switch
        {
            DiagnosticSeverity.Critical => DiagnosticFilter.Critical,
            DiagnosticSeverity.Warning => DiagnosticFilter.Warnings,
            DiagnosticSeverity.Suggestion => DiagnosticFilter.Suggestions,
            _ => DiagnosticFilter.None,
        };
    }
}
