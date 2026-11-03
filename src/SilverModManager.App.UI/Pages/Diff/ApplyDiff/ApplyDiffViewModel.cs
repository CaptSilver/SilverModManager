using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Controls.Trees.Files;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.App.UI.Controls.Trees;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

public partial class ApplyDiffViewModel : APageViewModel<IApplyDiffViewModel>, IApplyDiffViewModel
{
    private IServiceProvider _serviceProvider;
    private readonly ILogger<ApplyDiffViewModel> _logger;
    private DiffTreeViewModel? _fileTreeViewModel;
    private LoadoutId _loadoutId;

    [Reactive] public partial IViewModelInterface? BodyViewModel { get; set; }

    [Reactive] public partial bool LoadFailed { get; private set; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <inheritdoc />
    public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; }

    /// <inheritdoc />
    [Reactive] public partial IReadOnlyList<StrayFileNotice> StrayFiles { get; private set; } = [];

    /// <inheritdoc />
    public ReactiveCommand<Unit, Unit> RemoveStrayFilesCommand { get; }

    // True while an apply is running, which is the synchronizer's Pending state. NeedsSync is the normal state
    // whenever the preview has something to show, so it must not disable the command.
    private readonly BehaviorSubject<bool> _applyRunning = new(false);
    private Task<IObservable<LoadoutSynchronizerState>>? _status;

    public ApplyDiffViewModel(IWindowManager windowManager, IServiceProvider serviceProvider) : base(windowManager)
    {
        TabTitle = Language.ApplyDiffViewModel_PageTitle;
        TabIcon = IconValues.ListFilled;

        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<ApplyDiffViewModel>>();

        RefreshCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_fileTreeViewModel is null)
            {
                return;
            }

            await RefreshAsync(_fileTreeViewModel);
        });

        var selectionCanBeRemoved = this.WhenAnyValue(vm => vm.BodyViewModel)
            .Select(body => body is DiffTreeViewModel tree
                ? tree.WhenAnyValue(t => t.SelectedNodes).Select(nodes => nodes.Any(DiffTreeViewModel.CanRemove))
                : Observable.Return(false))
            .Switch();
        var canRemove = selectionCanBeRemoved.CombineLatest(_applyRunning, (selected, running) => selected && !running);
        RemoveSelectedCommand = ReactiveCommand.CreateFromTask(RemoveSelectedAsync, canRemove);

        var strayFilesPresent = this.WhenAnyValue(vm => vm.StrayFiles).Select(files => files.Count > 0);
        RemoveStrayFilesCommand = ReactiveCommand.CreateFromTask(RemoveStrayFilesAsync,
            strayFilesPresent.CombineLatest(_applyRunning, (present, running) => present && !running));

        // The status only matters while the page is on screen, so it is followed from activation to deactivation.
        this.WhenActivated(disposables =>
        {
            if (_status is null) return;
            Observable.FromAsync(() => _status)
                .Switch()
                .OnUI()
                .Subscribe(state => _applyRunning.OnNext(state == LoadoutSynchronizerState.Pending))
                ?.DisposeWith(disposables);

            // Any diagnostic that offers to remove a loadout file lands in the notice, whichever emitter it came from.
            _serviceProvider.GetRequiredService<IDiagnosticManager>().GetLoadoutDiagnostics(_loadoutId)
                .Select(StrayFilesOf)
                .OnUI()
                .Subscribe(files => StrayFiles = files)
                .DisposeWith(disposables);
        });
    }


    public void Initialize(LoadoutId loadoutId)
    {
        _loadoutId = loadoutId;
        _status = _serviceProvider.GetRequiredService<ISynchronizerService>().StatusForLoadout(loadoutId);
        _fileTreeViewModel = new DiffTreeViewModel(_loadoutId,
            _serviceProvider.GetRequiredService<ISynchronizerService>(),
            _serviceProvider.GetRequiredService<IConnection>()
        );

        // Nothing awaits the first refresh, so a failure (an invalid loadout, a sync error) has to be logged here.
        RefreshAsync(_fileTreeViewModel).FireAndForget(_logger);
    }

    // The refresh walks the install and the loadout; the tree's own bindings marshal to the UI thread, and the
    // continuation resumes on the caller's context, so BodyViewModel is assigned there.
    private async Task RefreshAsync(DiffTreeViewModel diffTreeViewModel)
    {
        try
        {
            await Task.Run(diffTreeViewModel.Refresh);
        }
        catch
        {
            // Without this the page stays blank when the first tree cannot be built; the caller still logs the error.
            LoadFailed = true;
            throw;
        }

        // The rebuilt tree holds new node instances, so nothing selected before is still in it. Cleared here rather
        // than in Refresh: the selection feeds the Delete button's can-execute, which has to tick on the UI thread.
        diffTreeViewModel.SelectedNodes = [];
        LoadFailed = false;
        BodyViewModel = diffTreeViewModel;
    }

    private static IReadOnlyList<StrayFileNotice> StrayFilesOf(IEnumerable<Diagnostic> diagnostics) => diagnostics
        .SelectMany(d => d.Actions)
        .Where(a => a.Id == DiagnosticActions.RemoveLoadoutFileId)
        .Select(a => DiagnosticActions.ParseRemoveLoadoutFile(a.Arguments))
        .Select(parsed => new StrayFileNotice(parsed.Path, parsed.Group))
        .Distinct()
        .OrderBy(notice => notice.Path.ToString(), StringComparer.Ordinal)
        .ToArray();

    // One request over every listed file, scoped to the groups the diagnostics named. The diagnostics refresh by
    // themselves once the removal is a revision of the loadout, which empties the notice.
    private async Task RemoveStrayFilesAsync()
    {
        var strays = StrayFiles;
        if (strays.Count == 0) return;

        var request = new FileRemovalRequest(_loadoutId, strays.Select(s => s.Path).Distinct().ToArray(), strays.Select(s => s.Group).Distinct().ToArray());
        if (!await LoadoutFileRemovalFlow.Run(_serviceProvider, WindowManager, request, Language.ApplyDiff_Remove_Activity)) return;
        if (_fileTreeViewModel is { } tree) await RefreshAfterRemovalAsync(tree);
    }

    /// <summary>
    /// Removes the selected rows through <see cref="LoadoutFileRemovalFlow"/> and refreshes the tree. Plans at the click
    /// rather than on selection: the tree's nodes are rebuilt on refresh, so a selection can be stale.
    /// </summary>
    private async Task RemoveSelectedAsync()
    {
        if (_fileTreeViewModel is not { } tree) return;
        var paths = tree.SelectedNodes.Where(DiffTreeViewModel.CanRemove).Select(n => n.Key).Distinct().ToArray();
        if (paths.Length == 0) return;

        var request = new FileRemovalRequest(_loadoutId, paths);
        if (!await LoadoutFileRemovalFlow.Run(_serviceProvider, WindowManager, request, Language.ApplyDiff_Remove_Activity)) return;

        await RefreshAfterRemovalAsync(tree);
    }

    // The removal already happened, so a failed refresh is reported like any other failure of the flow.
    private async Task RefreshAfterRemovalAsync(DiffTreeViewModel tree)
    {
        try
        {
            await RefreshAsync(tree);
        }
        catch (Exception e)
        {
            await _serviceProvider.GetRequiredService<IErrorPresenter>().Report(e, Language.ApplyDiff_Remove_Activity);
        }
    }
}
