using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Helpers;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk.EventBus;

using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Logging;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Controls.DevelopmentBuildBanner;
using SilverModManager.App.UI.Controls.SecretStoreNotice;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Controls.TopBar;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.LeftMenu;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Overlays.Updater;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.CLI;
using SilverModManager.CrossPlatform;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.UI.Sdk;
using R3;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Disposable = System.Reactive.Disposables.Disposable;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace SilverModManager.App.UI.Windows;

public partial class MainWindowViewModel : AViewModel<IMainWindowViewModel>, IMainWindowViewModel
{
    private readonly IWindowManager _windowManager;
    private readonly IConnection _connection;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWindowNotificationService _notificationService;

    /// <summary>Runs the files dropped on the window.</summary>
    public MainWindowDropHandler DropHandler { get; }

    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, bool> BringWindowToFront { get; }
    public ReactiveUI.ReactiveCommand<IStorageProvider, System.Reactive.Unit> RegisterStorageProvider { get; }
    public ReactiveUI.ReactiveCommand<IClipboard, System.Reactive.Unit> RegisterClipboard { get; }

    public MainWindowViewModel(
        IServiceProvider serviceProvider,
        IWindowManager windowManager,
        IOverlayController overlayController,
        ILoginManager loginManager,
        IEventBus eventBus,
        ISettingsManager settingsManager,
        IWindowNotificationService notificationService)
    {
        _serviceProvider = serviceProvider;
        var avaloniaInterop = serviceProvider.GetRequiredService<IAvaloniaInterop>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _notificationService = notificationService;

        // NOTE(erri120): can't use DI for VMs that require an active Window because
        // those VMs would be instantiated before this constructor gets called.
        // Use GetRequiredService<TVM> instead.
        _windowManager = windowManager;
        _windowManager.RegisterWindow(this);

        WorkspaceController = new WorkspaceController(
            window: this,
            serviceProvider: serviceProvider
        );

        DropHandler = new MainWindowDropHandler(
            new LibraryDropActions(serviceProvider.GetRequiredService<SilverModManager.Abstractions.Library.ILibraryService>(), _connection, WorkspaceController),
            notificationService,
            serviceProvider.GetService<ILogger<MainWindowViewModel>>());

        TopBar = serviceProvider.GetRequiredService<ITopBarViewModel>();
        TopBar.AddPanelDropDownViewModel = new AddPanelDropDownViewModel(WorkspaceController);

        Spine = serviceProvider.GetRequiredService<ISpineViewModel>();
        DevelopmentBuildBanner = serviceProvider.GetRequiredService<IDevelopmentBuildBannerViewModel>();
        SecretStoreNotice = serviceProvider.GetRequiredService<ISecretStoreNoticeViewModel>();

        BringWindowToFront = ReactiveCommand.Create(() => settingsManager.Get<BehaviorSettings>().BringWindowToFront);
        RegisterStorageProvider = ReactiveCommand.Create<IStorageProvider>(storageProvider => avaloniaInterop.RegisterStorageProvider(storageProvider));
        RegisterClipboard = ReactiveCommand.Create<IClipboard>(clipboard => avaloniaInterop.RegisterClipboard(clipboard));

        this.WhenActivated(d =>
        {
            ConnectErrors(serviceProvider).DisposeWith(d);

            var welcomeOverlayViewModel = WelcomeOverlayViewModel.CreateIfNeeded(serviceProvider);
            if (welcomeOverlayViewModel is not null) overlayController.Enqueue(welcomeOverlayViewModel);

            R3.Observable
                .Return(UpdateChecker.ShouldCheckForUpdate(settingsManager))
                .Where(shouldCheck => shouldCheck)
                .ObserveOnThreadPool()
                .SelectAwait(
                    selector: (_, cancellationToken) => UpdaterViewModel.CreateIfNeeded(serviceProvider, cancellationToken),
                    configureAwait: false
                )
                .ObserveOnUIThreadDispatcher()
                .WhereNotNull()
                .Subscribe(overlayController, static (overlay, overlayController) =>
                {
                    overlay.Controller = overlayController;
                    overlayController.Enqueue(overlay);
                })
                .AddTo(d);

            loginManager.IsLoggedInObservable
                .DistinctUntilChanged()
                .Where(isSignedIn => isSignedIn)
                .Select(_ => System.Reactive.Unit.Default)
                .InvokeReactiveCommand(BringWindowToFront)
                .DisposeWith(d);

            this.WhenAnyValue(vm => vm.Spine.LeftMenuViewModel)
                .BindToVM(this, vm => vm.LeftMenu)
                .DisposeWith(d);

            this.WhenAnyValue(vm => vm.IsActive)
                .Where(isActive => isActive)
                .Select(_ => this)
                .BindTo(_windowManager, manager => manager.ActiveWindow)
                .DisposeWith(d);
            
            // Enable automatic UserInfo refresh when window gains focus
            loginManager.RefreshOnObservable(
                this.WhenAnyValue(vm => vm.IsActive).ToObservable()
            ).DisposeWith(d);
            
            overlayController.WhenAnyValue(oc => oc.CurrentOverlay)
                .BindTo(this, vm => vm.CurrentOverlay)
                .DisposeWith(d);
            
            eventBus
                .ObserveMessages<CliMessages.CollectionAddStarted>()
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (_, self) =>
                {
                    using var disposable = self.BringWindowToFront.Execute(System.Reactive.Unit.Default).Subscribe();
                    
                    self._notificationService.ShowToast(Language.ToastNotification_Adding_new_Collection_to_Library);
                })
                .DisposeWith(d);
            
            eventBus
                .ObserveMessages<CliMessages.CollectionAddSucceeded>()
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (message, self) =>
                {
                    var workspaceController = self.WorkspaceController;

                    var tuple = self.GetWorkspaceIdForGame(workspaceController, message.Revision.Collection.GameId);
                    if (!tuple.HasValue) return;

                    var (loadoutId, workspaceId) = tuple.Value;

                    var pageData = new PageData
                    {
                        FactoryId = CollectionDownloadPageFactory.StaticId,
                        Context = new CollectionDownloadPageContext
                        {
                            TargetLoadout = loadoutId,
                            CollectionRevisionMetadataId = message.Revision,
                        },
                    };

                    var behavior = workspaceController.GetDefaultOpenPageBehavior(pageData, NavigationInput.Default);
                    workspaceController.OpenPage(workspaceId, pageData, behavior);

                    self._notificationService.ShowToast(
                        string.Format(Language.ToastNotification_Adding_collection____0_, message.Revision.Collection.Name),
                        ToastNotificationVariant.Success
                    );
                })
                .DisposeWith(d);
            
            eventBus
                .ObserveMessages<CliMessages.CollectionAddFailed>()
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (message, self) =>
                {
                    switch (message.Reason)
                    {
                        case FailureReason.GameNotManaged gameNotManaged:
                            self._notificationService.ShowToast(
                                string.Format(Language.ToastNotification_Collection_Add_failed___0__is_not_a_managed_game, gameNotManaged.Game),
                                ToastNotificationVariant.Failure  
                            );
                            return;
                        case FailureReason.Unknown unknown:
                            self._notificationService.ShowToast(
                                Language.ToastNotification_Collection_Add_failed__An_unknown_error_occurred,
                                ToastNotificationVariant.Failure
                            );
                            return;
                    }
                })  
                .DisposeWith(d);

            eventBus
                .ObserveMessages<CliMessages.ModDownloadStarted>()
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (message, self) =>
                {
                    using var _ = self.BringWindowToFront.Execute(System.Reactive.Unit.Default).Subscribe();
                    
                    self._notificationService.ShowToast(Language.ToastNotification_Mod_Download_started);
                })
                .DisposeWith(d);
            
            eventBus
                .ObserveMessages<CliMessages.ModDownloadSucceeded>()
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (message, self) =>
                {
                    self._notificationService.ShowToast(
                        string.Format(Language.ToastNotification_Mod_Download_Completed____0_, message.LibraryItem.Name),
                        ToastNotificationVariant.Success
                    );
                })
                .DisposeWith(d);
            
            eventBus
                .ObserveMessages<CliMessages.ModDownloadFailed>()
                .ObserveOnUIThreadDispatcher()
                .Subscribe(this, static (message, self) =>
                {
                    switch (message.Reason)
                    {
                        case FailureReason.NotLoggedIn:
                            self._notificationService.ShowToast(
                                Language.ToastNotification_Download_failed__User_is_not_logged_in,
                                ToastNotificationVariant.Failure
                            );
                            return;
                        case FailureReason.AlreadyExists alreadyExists:
                            self._notificationService.ShowToast(
                                string.Format(Language.ToastNotification_Download_skipped__file_already_exists____0__, alreadyExists.Name),
                                ToastNotificationVariant.Neutral
                            );
                            return;
                        case FailureReason.GameNotManaged gameNotManaged:
                            self._notificationService.ShowToast(
                                string.Format(Language.ToastNotification_Download_failed__game_is_not_managed____0_, gameNotManaged.Game),
                                ToastNotificationVariant.Failure
                            );
                            return;
                        case FailureReason.Unknown:
                            self._notificationService.ShowToast(
                                Language.ToastNotification_Download_failed__An_unknown_error_occurred,
                                ToastNotificationVariant.Failure
                            );
                            return;
                    }
                })  
                .DisposeWith(d);

            R3.Disposable.Create(this, vm =>
            {
                vm._windowManager.UnregisterWindow(vm);
            }).DisposeWith(d);

            if (!_windowManager.RestoreWindowState(this))
            {
                // NOTE(erri120): select home on startup if we didn't restore the previous state
                Spine.NavigateToHome();
            }
        });
    }

    /// <summary>Handles files dropped on the window. Per-file failures are reported by the handler; this catches the rest.</summary>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await DropHandler.HandleAsync(paths, CancellationToken.None);
        }
        catch (Exception e)
        {
            _serviceProvider.GetService<ILogger<MainWindowViewModel>>()?.LogError(e, "Handling dropped files failed");
            _notificationService.ShowToast($"Couldn't handle the dropped files: {e.Message}", ToastNotificationVariant.Failure);
        }
    }

    private Optional<(LoadoutId, WorkspaceId)> GetWorkspaceIdForGame(IWorkspaceController workspaceController, NexusModsGameId nexusModsGameId)
    {
        var game = NexusModsGameScope.ResolveNxmGame(
            _serviceProvider.GetServices<IGameData>(),
            nexusModsGameId,
            gameId => GetActiveLoadoutForGame(gameId).HasValue,
            _serviceProvider.GetRequiredService<ILogger<MainWindowViewModel>>()
        );
        if (!game.HasValue) return Optional<(LoadoutId, WorkspaceId)>.None;
        return GetWorkspaceIdForGame(workspaceController, game.Value.GameId);
    }

    private Optional<(LoadoutId, WorkspaceId)> GetWorkspaceIdForGame(IWorkspaceController workspaceController, GameId gameId)
    {
        if (workspaceController.ActiveWorkspace.Context is LoadoutContext existingLoadoutContext && IsCorrectLoadoutForGame(existingLoadoutContext.LoadoutId, gameId))
            return (existingLoadoutContext.LoadoutId, workspaceController.ActiveWorkspaceId);

        var loadoutId = GetActiveLoadoutForGame(gameId);
        if (!loadoutId.HasValue) return Optional<(LoadoutId, WorkspaceId)>.None;

        var workspaceViewModel = workspaceController.ChangeOrCreateWorkspaceByContext(
            predicate: loadoutContext => loadoutContext.LoadoutId.Equals(loadoutId.Value),
            getPageData: () => Optional<PageData>.None,
            getWorkspaceContext: () => new LoadoutContext
            {
                LoadoutId = loadoutId.Value,
            }
        );

        return (loadoutId.Value, workspaceViewModel.Id);
    }

    private bool IsCorrectLoadoutForGame(LoadoutId loadoutId, GameId gameId)
    {
        var loadout = Loadout.Load(_connection.Db, loadoutId);
        return loadout.IsValid() && loadout.Game.GameId == gameId;
    }

    private Optional<LoadoutId> GetActiveLoadoutForGame(GameId gameId)
    {
        var gameRegistry = _serviceProvider.GetRequiredService<IGameRegistry>();
        if (!gameRegistry.LocateGameInstallations().TryGetFirst(x => x.Game.GameId == gameId, out var gameInstallation)) return Optional<LoadoutId>.None;

        if (gameInstallation.Game is not IGame game) return Optional<LoadoutId>.None;
        return _serviceProvider.GetRequiredService<ILoadoutManager>().GetCurrentlyActiveLoadout(gameInstallation);
    }
    
    private IDisposable ConnectErrors(IServiceProvider provider)
    {
        var settings = provider.GetRequiredService<ISettingsManager>().Get<LoggingSettings>();
        if (!settings.ShowExceptions) return Disposable.Empty;

        var source = provider.GetService<IObservableExceptionSource>();
        if (source is null) return Disposable.Empty;

        return source.Exceptions
            .Subscribe(msg =>
                {
                    var title = "Unhandled Exception";
                    var description = msg.Message;
                    string? details = null;
                    if (msg.Exception != null)
                    {
                        details = $"""
                                  ```
                                  {msg.Exception}
                                  ``` 
                                  """;
                    }

                    Task.Run(() => MessageBoxOkViewModel.Show(provider, title, description, details));
                }
            );
    }

    internal void OnClose()
    {
        // NOTE(erri120): This gets called by the View and can't be inside the disposable
        // of the VM because the MainWindowViewModel gets disposed last, after its contents.
        _windowManager.SaveWindowState(this);
    }
    
    public WindowId WindowId { get; } = WindowId.NewId();

    /// <inheritdoc/>
    [Reactive] public partial bool IsActive { get; set; }
    
    [Reactive]
    public partial IOverlayViewModel? CurrentOverlay { get; set; }

    /// <inheritdoc/>
    public IWorkspaceController WorkspaceController { get; }

    [Reactive] public partial ISpineViewModel Spine { get; set; }

    [Reactive] public partial ILeftMenuViewModel? LeftMenu { get; set; }

    [Reactive]
    public partial ITopBarViewModel TopBar { get; set; }

    [Reactive]
    public partial IDevelopmentBuildBannerViewModel DevelopmentBuildBanner { get; set; }

    [Reactive]
    public partial ISecretStoreNoticeViewModel SecretStoreNotice { get; set; }
}
