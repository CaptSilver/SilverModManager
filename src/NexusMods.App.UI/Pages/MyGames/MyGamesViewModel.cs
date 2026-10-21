using NexusMods.App.UI.Helpers;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Controls.GameWidget;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.UI.Sdk.Icons;
using NexusMods.MnemonicDB.Abstractions;
using OneOf;
using OneOf.Types;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData.Aggregation;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Sdk.Settings;
using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.App.UI.Controls.MiniGameWidget.ComingSoon;
using NexusMods.App.UI.Controls.MiniGameWidget.Standard;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.App.UI.Settings;
using NexusMods.Collections;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;
using GameInstallMetadata = NexusMods.Sdk.Games.GameInstallMetadata;

namespace NexusMods.App.UI.Pages.MyGames;

[UsedImplicitly]
public class MyGamesViewModel : APageViewModel<IMyGamesViewModel>, IMyGamesViewModel
{
    private const string TrelloPublicRoadmapUrl = "https://trello.com/b/gPzMuIr3/nexus-mods-app-roadmap";

    private readonly ILibraryService _libraryService;
    private readonly CollectionDownloader _collectionDownloader;
    private readonly IWindowManager _windowManager;
    private readonly IJobMonitor _jobMonitor;
    private readonly IOverlayController _overlayController;
    private readonly IConnection _connection;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISynchronizerService _syncService;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IGameRegistry _gameRegistry;

    private ReadOnlyObservableCollection<IViewModelInterface> _supportedGames = new([]);
    private ReadOnlyObservableCollection<IGameWidgetViewModel> _installedGames = new([]);

    public ReactiveCommand<Unit, Unit> OpenRoadmapCommand { get; }
    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames => _installedGames;
    public ReadOnlyObservableCollection<IViewModelInterface> SupportedGames => _supportedGames;

    [Reactive] public string? LocatorProblemsNotice { get; private set; }

    public MyGamesViewModel(
        IWindowManager windowManager,
        IServiceProvider serviceProvider,
        IConnection conn,
        ILogger<MyGamesViewModel> logger,
        IOverlayController overlayController,
        IOSInterop osInterop,
        ISynchronizerService syncService,
        IGameRegistry gameRegistry) : base(windowManager)
    {
        var libraryDataProviders = serviceProvider.GetServices<ILibraryDataProvider>().ToArray();

        _collectionDownloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        _libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _overlayController = overlayController;
        _connection = conn;
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _gameRegistry = gameRegistry;

        TabTitle = Language.MyGames;
        TabIcon = IconValues.GamepadOutline;

        _serviceProvider = serviceProvider;
        _syncService = syncService;
        _windowManager = windowManager;

        OpenRoadmapCommand = ReactiveCommand.Create(() =>
        {
            var uri = new Uri(TrelloPublicRoadmapUrl);
            osInterop.OpenUri(uri);
        });

        this.WhenActivated(d =>
            {
                // Locating fills in Problems, so it has to run first.
                var installations = gameRegistry.LocateGameInstallations();
                LocatorProblemsNotice = FormatProblems(gameRegistry.Problems);

                installations
                    .ToReadOnlyObservableCollection()
                    .ToObservableChangeSet()
                    .Transform(installation =>
                        {
                            var vm = _serviceProvider.GetRequiredService<IGameWidgetViewModel>();
                            vm.Installation = installation;

                            vm.AddGameCommand = ReactiveCommand.CreateFromTask(async () => await AddGameHandler(installation, vm));

                            vm.RemoveAllLoadoutsCommand = ReactiveCommand.CreateFromTask(async () =>
                            {
                                if (GetJobRunningForGameInstallation(installation).IsT2) return;

                                var filesToDelete = libraryDataProviders.SelectMany(dataProvider => dataProvider.GetAllFiles(installation.Game.GameId)).ToArray();
                                var totalSize = filesToDelete.Sum(static Size (file) => file.Size);

                                var collections = installation.Game.CollectionsOf(conn.Db);

                                var overlay = new RemoveGameOverlayViewModel
                                {
                                    GameName = installation.Game.DisplayName,
                                    NumDownloads = filesToDelete.Length,
                                    SumDownloadsSize = totalSize,
                                    NumCollections = collections.Length,
                                };

                                var result = await overlayController.EnqueueAndWait(overlay);
                                if (!result.ShouldRemoveGame) return;

                                var stateBeforeRemoving = vm.State;
                                vm.State = GameWidgetState.RemovingGame;
                                var removed = true;
                                try
                                {
                                    removed = await MessageBoxOkViewModel.RunOrExplainRunningGame(
                                        _serviceProvider,
                                        installation.Game.DisplayName,
                                        () => Task.Run(async () => await RemoveGame(installation, shouldDeleteDownloads: result.ShouldDeleteDownloads, filesToDelete, collections))
                                    );
                                }
                                catch (SynchronizerPreflightException e)
                                {
                                    // Cleaning the game folder can't be done right now, but removing the game from
                                    // the app writes nothing there, so offer that instead of leaving it stuck.
                                    removed = await ShowRemoveFromAppOnlyDialog(installation, e.Message);
                                    if (removed)
                                    {
                                        removed = await MessageBoxOkViewModel.RunOrExplainRunningGame(
                                            _serviceProvider,
                                            installation.Game.DisplayName,
                                            () => Task.Run(async () => await RemoveGame(installation, shouldDeleteDownloads: result.ShouldDeleteDownloads, filesToDelete, collections, cleanGameFolder: false))
                                        );
                                    }
                                }

                                // On Cancel nothing was removed and the game is still managed.
                                vm.State = removed ? GameWidgetState.DetectedGame : stateBeforeRemoving;
                            });

                            vm.ViewGameCommand = ReactiveCommand.Create(() =>
                            {
                                NavigateToLoadoutLibrary(conn, installation);
                            });

                            vm.IsManagedObservable = Loadout.ObserveAll(conn)
                                .Filter(l => l.IsVisible() && l.InstallationInstance.Game.GameId == installation.Game.GameId && l.InstallationInstance.LocatorResult.Store == installation.LocatorResult.Store)
                                .Count()
                                .Select(c => c > 0);

                            var job = GetJobRunningForGameInstallation(installation);

                            // fixes when the page loads and a job is still running
                            vm.State = job.Value switch
                            {
                                CreateLoadoutJob _ => GameWidgetState.AddingGame,
                                UnmanageGameJob _ => GameWidgetState.RemovingGame,
                                _ => GameWidgetState.DetectedGame,
                            };

                            return vm;
                        }
                    )
                    .OnUI()
                    .Bind(out _installedGames)
                    .SubscribeWithErrorLogging()
                    .DisposeWith(d);

                var supportedGamesAsIGame = serviceProvider
                    .GetServices<IGameData>()
                    .Cast<IGame>()
                    .Where(game => _installedGames.All(install => install.Installation?.GetGame().GameId != game.GameId)); // Exclude found games

                var miniGameWidgetViewModels = supportedGamesAsIGame
                    .Select(game =>
                        {
                            var vm = _serviceProvider.GetRequiredService<IMiniGameWidgetViewModel>();
                            vm.Game = game;
                            vm.Name = game.DisplayName;
                            // is this supported game installed?
                            vm.IsFound = _installedGames.Any(install => install.Installation?.GetGame().GameId == game.GameId);
                            vm.GameInstallations = _installedGames
                                .Where(install => install.Installation?.GetGame().GameId == game.GameId)
                                .Select(install => install.Installation)
                                .NotNull()
                                .ToArray();
                            return vm;
                        }
                    )
                    .OrderByDescending(vm => vm.IsFound)
                    .ToList();

                var comingSoonMiniGameWidget = _serviceProvider.GetRequiredService<IComingSoonMiniGameWidgetViewModel>();

                // create a new ReadOnlyObservableCollection from miniGameWidgetViewModels and comingSoonMiniGameWidget
                _supportedGames = new ReadOnlyObservableCollection<IViewModelInterface>(
                    new ObservableCollection<IViewModelInterface>(miniGameWidgetViewModels) {
                    // Add the coming soon widget to the end of the list
                    comingSoonMiniGameWidget,
                });
            }
        );
    }

    private static string? FormatProblems(ImmutableArray<GameLocatorProblem> problems)
    {
        if (problems.IsDefaultOrEmpty) return null;
        return string.Join(Environment.NewLine, problems.Select(problem => problem.Message));
    }

    private OneOf<None, CreateLoadoutJob, UnmanageGameJob> GetJobRunningForGameInstallation(GameInstallation installation)
    {
        foreach (var job in _jobMonitor.Jobs)
        {
            if (job.Status != JobStatus.Running) continue;

            if (job.Definition is CreateLoadoutJob createLoadoutJob && createLoadoutJob.Installation.Equals(installation)) return createLoadoutJob;
            if (job.Definition is UnmanageGameJob unmanageGameJob && unmanageGameJob.Installation.Equals(installation)) return unmanageGameJob;
        }

        return OneOf<None, CreateLoadoutJob, UnmanageGameJob>.FromT0(new None());
    }

    private async Task RemoveGame(GameInstallation installation, bool shouldDeleteDownloads, LibraryFile.ReadOnly[] filesToDelete, CollectionMetadata.ReadOnly[] collections, bool cleanGameFolder = true)
    {
        await _syncService.UnManage(installation, cleanGameFolder: cleanGameFolder);

        if (!shouldDeleteDownloads) return;
        await _libraryService.RemoveLibraryItems(filesToDelete.Select(file => file.AsLibraryItem()));

        foreach (var collection in collections)
        {
            await _collectionDownloader.DeleteCollection(collection);
        }
    }
    
    private async Task AddGameHandler(GameInstallation installation, IGameWidgetViewModel vm)
    {
        if (GetJobRunningForGameInstallation(installation).IsT1) return;

        vm.State = GameWidgetState.AddingGame;
        Loadout.ReadOnly loadout;
        try
        {
            loadout = await Task.Run(async () => await ManageGame(installation));
        }
        catch (SynchronizerPreflightException e)
        {
            // Nothing was written, so the game goes back to being a detected game with the reason shown.
            vm.State = GameWidgetState.DetectedGame;
            await ShowCannotAddGameDialog(installation, e.Message);
            return;
        }

        // Check if there are external changes
        var changeEntries = await GetExternalChangesItems(loadout);
        vm.State = GameWidgetState.ManagedGame;
        var buildCovered = BuildIsCovered(installation);
        
        // Offer to clean them up
        if (changeEntries.Length > 0)
        {
            var (revert, doNothing, clean) = (ButtonDefinitionId.Cancel, ButtonDefinitionId.From("doNothing"), ButtonDefinitionId.Accept);
            var result = await ShowCleanGameFolderDialog(revert,
                doNothing,
                clean,
                changeEntries,
                installation,
                buildCovered
            );
            
            if (result == revert)
            {
                // Revert the loadout creation
                vm.State = GameWidgetState.RemovingGame;
                var reverted = await MessageBoxOkViewModel.RunOrExplainRunningGame(
                    _serviceProvider,
                    installation.Game.DisplayName,
                    () => Task.Run(async () => await _syncService.UnManage(installation, cleanGameFolder: false))
                );
                vm.State = reverted ? GameWidgetState.DetectedGame : GameWidgetState.ManagedGame;

                // A refused revert leaves the game managed, so carry on to its library like the other answers do.
                if (reverted) return;
            }
            if (result == clean)
            {
                vm.State = GameWidgetState.AddingGame;
                try
                {
                    await _loadoutManager.CleanGameFolder(loadout);
                }
                catch (SynchronizerPreflightException e)
                {
                    // Nothing was deleted; the files stay as External Changes and the game stays managed.
                    await ShowRefusalDialog($"Can't clean the {installation.Game.DisplayName} folder", e.Message);
                }
                vm.State = GameWidgetState.ManagedGame;
            }
            
            // do nothing, so keep the files
        }
        
        NavigateToLoadoutLibrary(_connection, installation);
    }
    
    private ValueTask<LoadoutItemWithTargetPath.ReadOnly[]> GetExternalChangesItems(Loadout.ReadOnly loadout)
    {
        var db = _connection.Db;
        if (!LoadoutOverridesGroup.FindByOverridesFor(db, loadout.Id).TryGetFirst(out var overrideGroup))
            return ValueTask.FromResult<LoadoutItemWithTargetPath.ReadOnly[]>([]);

        return ValueTask.FromResult(overrideGroup.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath().ToArray());
    }
    
    private async Task<ButtonDefinitionId> ShowCleanGameFolderDialog(
        ButtonDefinitionId revert,
        ButtonDefinitionId doNothing,
        ButtonDefinitionId clean,
        LoadoutItemWithTargetPath.ReadOnly[] changeEntries,
        GameInstallation installation,
        bool buildCovered)
    {
        var prompt = CleanFolderPrompt.For(buildCovered, changeEntries.Length, installation.Game.DisplayName);
        var markdownVm = _serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdownVm.Contents = prompt.Markdown;

        var keep = new DialogButtonDefinition("Keep existing files", doNothing, ButtonAction.Reject);
        var dialog = DialogFactory.CreateStandardDialog(
            title: prompt.Title,
            new StandardDialogParameters()
            {
                Markdown = markdownVm,
            },
            buttonDefinitions: prompt.OffersClean
                ?
                [
                    new DialogButtonDefinition("Cancel", revert),
                    keep,
                    new DialogButtonDefinition("Clean folder", clean, ButtonAction.Accept, ButtonStyling.Primary),
                ]
                :
                [
                    new DialogButtonDefinition("Cancel", revert),
                    keep,
                ]
        );
        
        return (await _windowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId;
    }
    
    private Task ShowCannotAddGameDialog(GameInstallation installation, string message)
    {
        return ShowRefusalDialog($"{installation.Game.DisplayName} can't be added yet", message);
    }

    private async Task ShowRefusalDialog(string title, string message)
    {
        var markdownVm = _serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdownVm.Contents = message;

        var dialog = DialogFactory.CreateStandardDialog(
            title: title,
            new StandardDialogParameters { Markdown = markdownVm },
            buttonDefinitions: [new DialogButtonDefinition("OK", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary)]
        );

        await _windowManager.ShowDialog(dialog, DialogWindowType.Modal);
    }

    /// <summary>
    /// Asks whether to remove the game without cleaning its game folder, because cleaning is not possible.
    /// </summary>
    private async Task<bool> ShowRemoveFromAppOnlyDialog(GameInstallation installation, string reason)
    {
        var markdownVm = _serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdownVm.Contents = $"""
            {reason}

            Until that is fixed the game folder can't be cleaned, but you can still remove {installation.Game.DisplayName} from the app only.
            #### Important
            - Mod files the app already deployed stay where they are.
            - To get the vanilla game files back, verify them in Steam ("Verify integrity of game files") or in the game's launcher.
            """;

        var dialog = DialogFactory.CreateStandardDialog(
            title: $"Can't clean the {installation.Game.DisplayName} folder",
            new StandardDialogParameters { Markdown = markdownVm },
            buttonDefinitions:
            [
                new DialogButtonDefinition("Cancel", ButtonDefinitionId.Cancel),
                new DialogButtonDefinition("Remove from the app only", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
            ]
        );

        return (await _windowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId == ButtonDefinitionId.Accept;
    }

    private bool BuildIsCovered(GameInstallation installation)
    {
        try
        {
            installation.GetGame().Synchronizer.EnsureBuildCovered(installation, installation.LocatorResult.LocatorIds.Distinct().ToArray());
            return true;
        }
        catch (SynchronizerPreflightException)
        {
            return false;
        }
    }

    private async Task<Loadout.ReadOnly> ManageGame(GameInstallation installation)
    {
        return await _loadoutManager.CreateLoadout(installation);
    }

    private Optional<LoadoutId> GetLoadout(IConnection conn, GameInstallation installation)
    {
        if (!_gameRegistry.TryGetMetadata(installation, out var metadata)) return Optional<LoadoutId>.None;
        if (metadata.Contains(GameInstallMetadata.LastSyncedLoadout))
        {
            return metadata.LastSyncedLoadout.LoadoutId;
        }

        // no applied loadout, return the first one
        var loadout = Loadout.All(conn.Db).FirstOrOptional(loadout => loadout.IsVisible() && loadout.InstallationInstance.Equals(installation));
        return loadout.HasValue ? loadout.Value.LoadoutId : Optional<LoadoutId>.None;
    }
    
    private void NavigateToLoadoutLibrary(IConnection conn, GameInstallation installation)
    {
        var fistLoadout = GetLoadout(conn, installation);
        if (!fistLoadout.HasValue) return;
        var loadoutId = fistLoadout.Value;
        Dispatcher.UIThread.Invoke(() =>
            {
                var workspaceController = _windowManager.ActiveWorkspaceController;
                
                workspaceController.ChangeOrCreateWorkspaceByContext(
                    context => context.LoadoutId == loadoutId,
                    () => new PageData
                    {
                        FactoryId = LibraryPageFactory.StaticId,
                        Context = new LibraryPageContext()
                        {
                            LoadoutId = loadoutId,
                        },
                    },
                    () => new LoadoutContext
                    {
                        LoadoutId = loadoutId,
                    }
                );
            }
        );
    }
}
