using SilverModManager.App.UI.Helpers;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.GameWidget;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using OneOf;
using OneOf.Types;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData.Aggregation;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Sdk.Settings;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Controls.MiniGameWidget.ComingSoon;
using SilverModManager.App.UI.Controls.MiniGameWidget.Standard;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Settings;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using GameInstallMetadata = SilverModManager.Sdk.Games.GameInstallMetadata;

namespace SilverModManager.App.UI.Pages.MyGames;

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

    private readonly SourceList<GameInstallation> _installations = new();
    private ReadOnlyObservableCollection<IGameWidgetViewModel> _installedGames = new([]);

    public ReactiveCommand<Unit, Unit> OpenRoadmapCommand { get; }
    public ReactiveCommand<Unit, Unit> AddGameFolderCommand { get; }
    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames => _installedGames;
    [Reactive] public ReadOnlyObservableCollection<IViewModelInterface> SupportedGames { get; private set; } = new([]);

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

        AddGameFolderCommand = ReactiveCommand.CreateFromTask(AddGameFolder);

        this.WhenActivated(d =>
            {
                _installations.Connect()
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

                RefreshInstalledGames();
            }
        );
    }

    /// <summary>
    /// Locates the installed games again and updates both lists. Activation and the add-a-folder command share it,
    /// so a game added by folder shows up without leaving the page.
    /// </summary>
    private void RefreshInstalledGames()
    {
        // Locating fills in Problems, so it has to run first.
        var installations = _gameRegistry.LocateGameInstallations();
        LocatorProblemsNotice = FormatProblems(_gameRegistry.Problems);

        // By reference: the registry hands back the same instances until its cache is cleared, and the record's
        // value equality would hash every location of every game.
        _installations.EditDiff(installations, ReferenceEqualityComparer.Instance);
        SupportedGames = BuildSupportedGames(installations);
    }

    private ReadOnlyObservableCollection<IViewModelInterface> BuildSupportedGames(ImmutableArray<GameInstallation> installations)
    {
        var supportedGamesAsIGame = _serviceProvider
            .GetServices<IGameData>()
            .Cast<IGame>()
            .Where(game => installations.All(install => install.Game.GameId != game.GameId)); // Exclude found games

        var miniGameWidgetViewModels = supportedGamesAsIGame
            .Select(game =>
                {
                    var vm = _serviceProvider.GetRequiredService<IMiniGameWidgetViewModel>();
                    vm.Game = game;
                    vm.Name = game.DisplayName;
                    // is this supported game installed?
                    vm.IsFound = installations.Any(install => install.Game.GameId == game.GameId);
                    vm.GameInstallations = installations
                        .Where(install => install.Game.GameId == game.GameId)
                        .ToArray();
                    return vm;
                }
            )
            .OrderByDescending(vm => vm.IsFound)
            .ToList();

        var comingSoonMiniGameWidget = _serviceProvider.GetRequiredService<IComingSoonMiniGameWidgetViewModel>();

        // create a new ReadOnlyObservableCollection from miniGameWidgetViewModels and comingSoonMiniGameWidget
        return new ReadOnlyObservableCollection<IViewModelInterface>(
            new ObservableCollection<IViewModelInterface>(miniGameWidgetViewModels) {
            // Add the coming soon widget to the end of the list
            comingSoonMiniGameWidget,
        });
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
    
    private async Task AddGameFolder()
    {
        var interop = _serviceProvider.GetRequiredService<IAvaloniaInterop>();
        var picked = await interop.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a game folder",
            AllowMultiple = false,
        });
        if (picked.Length == 0) return;

        var folder = picked[0];
        var service = _serviceProvider.GetRequiredService<IAddGameByPath>();

        GameId? chosen = null;
        while (true)
        {
            // The service reads the disk and writes installs.yaml, so keep it off the UI thread.
            var game = chosen;
            var result = await Task.Run(() => service.Add(folder, game, prefix: null, wine: null));
            var prompt = AddGamePrompt.For(result, folder);

            if (result is AddGameResult.Added) RefreshInstalledGames();

            if (prompt.Candidates.Count == 0)
            {
                await ShowRefusalDialog(prompt.Title, prompt.Markdown);
                return;
            }

            chosen = await AskWhichGame(prompt);
            if (chosen is null) return;
        }
    }

    /// <summary>
    /// Shows one button per candidate and returns the game picked, or null when the dialog was dismissed.
    /// </summary>
    private async Task<GameId?> AskWhichGame(AddGamePrompt prompt)
    {
        var markdownVm = _serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
        markdownVm.Contents = prompt.Markdown;

        var buttons = prompt.Candidates
            .Select(candidate => new DialogButtonDefinition(candidate.Label, ButtonDefinitionId.From($"game:{candidate.Game}"), ButtonAction.Accept))
            .Append(new DialogButtonDefinition("Cancel", ButtonDefinitionId.Cancel))
            .ToArray();

        var dialog = DialogFactory.CreateStandardDialog(
            title: prompt.Title,
            new StandardDialogParameters { Markdown = markdownVm },
            buttonDefinitions: buttons
        );

        var pressed = (await _windowManager.ShowDialog(dialog, DialogWindowType.Modal)).ButtonId;
        foreach (var candidate in prompt.Candidates)
        {
            if (pressed == ButtonDefinitionId.From($"game:{candidate.Game}")) return candidate.Game;
        }
        return null;
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
