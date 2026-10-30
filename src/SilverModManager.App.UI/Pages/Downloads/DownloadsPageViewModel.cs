using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Jobs;
using SilverModManager.UI.Sdk.Icons;
using ObservableCollections;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.Downloads;

public partial class DownloadsPageViewModel : APageViewModel<IDownloadsPageViewModel>, IDownloadsPageViewModel
{
    [Reactive] public partial int SelectionCount { get; private set; } = 0;
    
    [Reactive] public partial bool IsEmptyStateActive { get; set; } = true;
    
    public string HeaderTitle { get; private set; } = string.Empty;
    public string HeaderDescription { get; private set; } = string.Empty;

    public DownloadsTreeDataGridAdapter Adapter { get; }
    public ReactiveCommand<Unit> PauseAllCommand { get; }
    public ReactiveCommand<Unit> ResumeAllCommand { get; }
    public ReactiveCommand<Unit> PauseSelectedCommand { get; }
    public ReactiveCommand<Unit> ResumeSelectedCommand { get; }
    public ReactiveCommand<Unit> CancelSelectedCommand { get; }
    public Observable<bool> SelectionHasRunningItems { get; }
    public Observable<bool> SelectionHasPausedItems { get; }
    public Observable<bool> SelectionHasActiveItems { get; }
    public Observable<bool> HasRunningItems { get; }
    public Observable<bool> HasPausedItems { get; }

    public DownloadsPageViewModel(IWindowManager windowManager, IServiceProvider serviceProvider, DownloadsPageContext context) : base(windowManager)
    {
        var downloadsService = serviceProvider.GetRequiredService<IDownloadsService>();

        // Create filter based on context
        var filter = context.GameScope.HasValue
            ? DownloadsFilter.ForGame(context.GameScope.Value)
            : DownloadsFilter.All();

        var downloadsDataProvider = serviceProvider.GetRequiredService<IDownloadsDataProvider>();
        Adapter = new DownloadsTreeDataGridAdapter(serviceProvider, downloadsDataProvider, filter);
        
        TabTitle = Language.Downloads_WorkspaceTitle;
        TabIcon = IconValues.PictogramDownload;
        
        // Set header title and description based on context
        (HeaderTitle, HeaderDescription) = context.GameScope.HasValue
            ? (string.Format(Language.DownloadsLeftMenu_GameSpecificDownloads, downloadsDataProvider.ResolveGameName(context.GameScope.Value)), Language.DownloadsPage_GameSpecificDownloads_Description)
            : (Language.DownloadsLeftMenu_AllDownloads, Language.DownloadsPage_AllDownloads_Description);

        var isActive = new R3.ReactiveProperty<bool>(false);
        Observable<bool> WhileActive(Observable<bool> canRun) => isActive.Select(canRun, static (active, source) => active ? source : R3.Observable.Return(false)).Switch();

        // Create observables and commands
        var runningCountChanges = downloadsService.GetDownloadsByStatus(JobStatus.Running)
            // NOTE(sewer): This is a hack.
            // Explanation: https://github.com/Nexus-Mods/NexusMods.App/pull/3898#discussion_r2387773402
            .Delay(TimeSpan.FromMilliseconds(64))
            .OnUI()
            .Select(_ => Unit.Default);
        
        // Create observables that track selection and status changes
        var selectionStatusReevaluationTrigger = this.WhenAnyValue(vm => vm.SelectionCount)
            .Select(_ => Unit.Default)
            .Merge(runningCountChanges);

        SelectionHasRunningItems = selectionStatusReevaluationTrigger
            .Select(_ => Adapter.SelectedModels
                .Select(model => model.GetOptional<DownloadComponents.StatusComponent>(DownloadColumns.Status.ComponentKey))
                .Where(opt => opt.HasValue)
                .Any(opt => opt.Value.Status.Value == JobStatus.Running))
            .ToObservable();
        
        SelectionHasPausedItems = selectionStatusReevaluationTrigger
            .Select(_ => Adapter.SelectedModels
                .Select(model => model.GetOptional<DownloadComponents.StatusComponent>(DownloadColumns.Status.ComponentKey))
                .Where(opt => opt.HasValue)
                .Any(opt => opt.Value.Status.Value == JobStatus.Paused))
            .ToObservable();
        
        SelectionHasActiveItems = selectionStatusReevaluationTrigger
            .Select(_ => Adapter.SelectedModels
                .Select(model => model.GetOptional<DownloadComponents.StatusComponent>(DownloadColumns.Status.ComponentKey))
                .Where(opt => opt.HasValue)
                .Any(opt => opt.Value.Status.Value.IsActive()))
            .ToObservable();
        
        // Create context-aware observables for running and paused items
        HasRunningItems = context.GameScope.HasValue
            ? downloadsService.GetRunningDownloadsForGame(context.GameScope.Value)
                .QueryWhenChanged(items => items.Count > 0)
                .OnUI()
                .ToObservable()
                .Prepend(false)
            : downloadsService.GetDownloadsByStatus(JobStatus.Running)
                .QueryWhenChanged(items => items.Count > 0)
                .OnUI()
                .ToObservable()
                .Prepend(false);
        
        HasPausedItems = context.GameScope.HasValue
            ? downloadsService.GetPausedDownloadsForGame(context.GameScope.Value)
                .QueryWhenChanged(items => items.Count > 0)
                .OnUI()
                .ToObservable()
                .Prepend(false)
            : downloadsService.GetDownloadsByStatus(JobStatus.Paused)
                .QueryWhenChanged(items => items.Count > 0)
                .OnUI()
                .ToObservable()
                .Prepend(false);

        // Commands with CanExecute - context-aware implementation. They are built once and only
        // watch the downloads while the page is active, so a hidden page holds no subscriptions.
        PauseAllCommand = context.GameScope.HasValue
            ? WhileActive(HasRunningItems).ToReactiveCommand<Unit>(_ => downloadsService.PauseAllForGame(context.GameScope.Value))
            : WhileActive(HasRunningItems).ToReactiveCommand<Unit>(_ => downloadsService.PauseAll());
        
        ResumeAllCommand = context.GameScope.HasValue
            ? WhileActive(HasPausedItems).ToReactiveCommand<Unit>(_ => downloadsService.ResumeAllForGame(context.GameScope.Value))
            : WhileActive(HasPausedItems).ToReactiveCommand<Unit>(_ => downloadsService.ResumeAll());
        
        PauseSelectedCommand = WhileActive(SelectionHasRunningItems).ToReactiveCommand<Unit>(
            executeAsync: (_, _) =>
            {
                foreach (var model in Adapter.SelectedModels)
                {
                    var downloadRef = model.GetOptional<DownloadRef>(DownloadColumns.DownloadRefComponentKey);
                    var statusComponent = model.GetOptional<DownloadComponents.StatusComponent>(DownloadColumns.Status.ComponentKey);
                    
                    // Only pause downloads that are currently running
                    if (downloadRef.HasValue && statusComponent is { HasValue: true, Value.Status.Value: JobStatus.Running })
                        downloadsService.PauseDownload(downloadRef.Value.Download);
                }

                return ValueTask.CompletedTask;
            },
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );
        
        ResumeSelectedCommand = WhileActive(SelectionHasPausedItems).ToReactiveCommand<Unit>(
            executeAsync: (_, _) =>
            {
                foreach (var model in Adapter.SelectedModels)
                {
                    var downloadRef = model.GetOptional<DownloadRef>(DownloadColumns.DownloadRefComponentKey);
                    var statusComponent = model.GetOptional<DownloadComponents.StatusComponent>(DownloadColumns.Status.ComponentKey);
                    
                    // Only resume downloads that are currently paused
                    if (downloadRef.HasValue && statusComponent.HasValue && statusComponent.Value.Status.Value == JobStatus.Paused)
                        downloadsService.ResumeDownload(downloadRef.Value.Download);
                }

                return ValueTask.CompletedTask;
            },
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );

        CancelSelectedCommand = WhileActive(SelectionHasActiveItems).ToReactiveCommand<Unit>(
            executeAsync: (_, _) =>
            {
                foreach (var model in Adapter.SelectedModels)
                {
                    var downloadRef = model.GetOptional<DownloadRef>(DownloadColumns.DownloadRefComponentKey);
                    var statusComponent = model.GetOptional<DownloadComponents.StatusComponent>(DownloadColumns.Status.ComponentKey);
                    
                    // Only cancel downloads that are currently active (running or paused)
                    if (downloadRef.HasValue && statusComponent.HasValue && statusComponent.Value.Status.Value.IsActive())
                        downloadsService.CancelDownload(downloadRef.Value.Download);
                }

                return ValueTask.CompletedTask;
            },
            awaitOperation: AwaitOperation.Parallel,
            initialCanExecute: false,
            configureAwait: false
        );

        this.WhenActivated(disposables =>
        {
            isActive.Value = true;
            Disposable.Create(isActive, static active => active.Value = false).AddTo(disposables);

            Adapter.Activate().AddTo(disposables);
            
            // Track selection count using count property
            Adapter.SelectedModels
                .ObserveCountChanged(notifyCurrentCount: true)
                .Subscribe(count => SelectionCount = count)
                .AddTo(disposables);

            // Track empty state
            Adapter.IsSourceEmpty
                .Subscribe(isEmpty => IsEmptyStateActive = isEmpty)
                .AddTo(disposables);

            // Subscribe to adapter messages for individual download actions
            Adapter.MessageSubject.Subscribe(
                (message) =>
                {
                    message.Switch(
                        pauseMessage =>
                        {
                            foreach (var download in pauseMessage.Downloads)
                                downloadsService.PauseDownload(download);
                        },
                        resumeMessage =>
                        {
                            foreach (var download in resumeMessage.Downloads)
                                downloadsService.ResumeDownload(download);
                        },
                        cancelMessage =>
                        {
                            foreach (var download in cancelMessage.Downloads)
                                downloadsService.CancelDownload(download);
                        }
                    );
                }
            ).AddTo(disposables);
        });
    }
}
