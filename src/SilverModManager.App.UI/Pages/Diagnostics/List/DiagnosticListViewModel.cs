using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Controls.Diagnostics;
using SilverModManager.Platform.Proton.Support;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Reloaded.Memory.Extensions;
using ObservableExtensions = R3.ObservableExtensions;

namespace SilverModManager.App.UI.Pages.Diagnostics;

[UsedImplicitly]
internal class DiagnosticListViewModel : APageViewModel<IDiagnosticListViewModel>, IDiagnosticListViewModel
{
    [Reactive] private Diagnostic[] Diagnostics { get; set; } = Array.Empty<Diagnostic>();

    [Reactive] public IDiagnosticEntryViewModel[] DiagnosticEntries { get; private set; } = Array.Empty<IDiagnosticEntryViewModel>();

    [Reactive] public LoadoutId LoadoutId { get; set; }

    [Reactive] public int NumCritical { get; private set; }
    [Reactive] public int NumWarnings { get; private set; }
    [Reactive] public int NumSuggestions { get; private set; }

    [Reactive] public DiagnosticFilter Filter { get; set; }

    [Reactive] public string SupportBundleMessage { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> CopySupportBundleCommand { get; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    private readonly ObservableAsPropertyHelper<bool> _isRefreshing;
    public bool IsRefreshing => _isRefreshing.Value;

    /// <summary>
    /// How long window activations have to stay quiet before the diagnostics are refreshed; alt-tabbing
    /// through the window produces several in a row.
    /// </summary>
    private static readonly TimeSpan ActivationDebounce = TimeSpan.FromMilliseconds(500);


    private const DiagnosticFilter AllFilter = DiagnosticFilter.Critical | DiagnosticFilter.Warnings | DiagnosticFilter.Suggestions;

    [Reactive] private DiagnosticSettings Settings { get; set; }

    public DiagnosticListViewModel(
        IServiceProvider serviceProvider,
        IWindowManager windowManager,
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

        TabIcon = IconValues.Cardiology;
        TabTitle = Language.DiagnosticListViewModel_DiagnosticListViewModel_Diagnostics;

        Settings = settingsManager.Get<DiagnosticSettings>();
        ObservableExtensions.AsSystemObservable(settingsManager.GetChanges<DiagnosticSettings>(prependCurrent: false)).BindToVM(this, vm => vm.Settings);

        this.WhenActivated(disposable =>
        {
            Filter = AllFilter;

            var serialDisposable = new SerialDisposable();
            serialDisposable.DisposeWith(disposable);

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
                .Select(diagnostics => diagnostics
                    .Select(diagnostic => (IDiagnosticEntryViewModel)new DiagnosticEntryViewModel(diagnostic, diagnosticWriter))
                    .ToArray()
                )
                .BindToVM(this, vm => vm.DiagnosticEntries)
                .DisposeWith(disposable);

            var severityCountObservable = this.WhenAnyValue(vm => vm.Diagnostics)
                .Select(diagnostics => diagnostics
                    .Select(diagnostic => diagnostic.Severity)
                    .GroupBy(x => x)
                    .ToDictionary(group => group.Key, group => group.Count())
                )
                .Replay(bufferSize: 1);

            severityCountObservable.Connect().DisposeWith(disposable);

            // diagnostic counts
            severityCountObservable
                .Select(dict => dict.GetValueOrDefault(DiagnosticSeverity.Suggestion, defaultValue: 0))
                .Select(num => Settings.MinimumSeverity > DiagnosticSeverity.Suggestion ? 0 : num)
                .BindToVM(this, vm => vm.NumSuggestions)
                .DisposeWith(disposable);

            severityCountObservable
                .Select(dict => dict.GetValueOrDefault(DiagnosticSeverity.Warning, defaultValue: 0))
                .Select(num => Settings.MinimumSeverity > DiagnosticSeverity.Warning ? 0 : num)
                .BindToVM(this, vm => vm.NumWarnings)
                .DisposeWith(disposable);

            severityCountObservable
                .Select(dict => dict.GetValueOrDefault(DiagnosticSeverity.Critical, defaultValue: 0))
                .Select(num => Settings.MinimumSeverity > DiagnosticSeverity.Critical ? 0 : num)
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
