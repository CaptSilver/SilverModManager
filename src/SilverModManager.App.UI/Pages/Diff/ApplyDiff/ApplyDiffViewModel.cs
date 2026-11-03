using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // The status only matters while the page is on screen, so it is followed from activation to deactivation.
        this.WhenActivated(disposables =>
        {
            if (_status is null) return;
            Observable.FromAsync(() => _status)
                .Switch()
                .OnUI()
                .Subscribe(state => _applyRunning.OnNext(state == LoadoutSynchronizerState.Pending))
                ?.DisposeWith(disposables);
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

    /// <summary>
    /// Plans from the current selection, asks, removes, refreshes. Plans at the click rather than on selection: the
    /// database moves between a selection and a click, and the tree's nodes are rebuilt on refresh. Every failure,
    /// a refusal included, goes to the error presenter; nothing is rethrown, since an exception out of a command
    /// lands in a stream nobody listens to.
    /// </summary>
    private async Task RemoveSelectedAsync()
    {
        if (_fileTreeViewModel is not { } tree) return;
        var paths = tree.SelectedNodes.Where(DiffTreeViewModel.CanRemove).Select(n => n.Key).Distinct().ToArray();
        if (paths.Length == 0) return;

        var request = new FileRemovalRequest(_loadoutId, paths);
        var remover = _serviceProvider.GetRequiredService<ILoadoutFileRemover>();
        try
        {
            var plan = await Task.Run(() => remover.Plan(request));
            if (!plan.HasAnythingToRemove(includeLocked: true))
                throw new FileRemovalRefusedException(plan.RefusalMessage());

            var dialog = DialogFactory.CreateStandardDialog(
                Language.ApplyDiff_Remove_Title,
                new StandardDialogParameters { Text = FileRemovalText.For(plan) },
                [
                    DialogStandardButtons.Cancel,
                    new DialogButtonDefinition(Language.ApplyDiff_Remove_Confirm, ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive),
                ]);
            var answer = await WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (answer.ButtonId != ButtonDefinitionId.Accept) return;

            await Task.Run(() => remover.Remove(request, includeLocked: true));
            await RefreshAsync(tree);
            _serviceProvider.GetRequiredService<IWindowNotificationService>().ShowToast(Language.ApplyDiff_Remove_Toast);
        }
        catch (Exception e)
        {
            await _serviceProvider.GetRequiredService<IErrorPresenter>().Report(e, Language.ApplyDiff_Remove_Activity);
        }
    }
}
