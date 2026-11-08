using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Controls;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.Wabbajack;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.LibraryPage.Collections;

public partial class WabbajackModlistCardViewModel : AViewModel<IWabbajackModlistCardViewModel>, IWabbajackModlistCardViewModel
{
    private static readonly TimeSpan RevisionThrottle = TimeSpan.FromMilliseconds(500);

    private readonly IConnection _connection;
    private readonly IServiceProvider _provider;
    private readonly IWindowManager _windowManager;
    private readonly WorkspaceId _workspaceId;
    private readonly LoadoutId _loadout;
    private readonly ILibraryArchiveLocator _locator;

    public WabbajackModlistCardViewModel(
        IServiceProvider provider,
        IWindowManager windowManager,
        WorkspaceId workspaceId,
        WabbajackModlistLibraryFile.ReadOnly file,
        LoadoutId loadout)
    {
        _provider = provider;
        _connection = provider.GetRequiredService<IConnection>();
        _locator = provider.GetRequiredService<ILibraryArchiveLocator>();
        _windowManager = windowManager;
        _workspaceId = workspaceId;
        _loadout = loadout;

        File = LibraryFileId.From(file.Id);
        Name = file.Name;
        Author = file.Author;
        Version = file.Version;
        ArchiveSize = file.ArchiveSize;
        InstallSize = file.InstallSize;
        ArchivesNeeded = file.ArchiveCount;
        // Only a list fetched from the gallery came from Wabbajack; one the user added from disk is a local file.
        Source = file.Contains(WabbajackModlistLibraryFile.GalleryDownloadUrl) ? ModSources.Wabbajack : ModSources.LocalFile;
        GameName = GameNameFor(provider, file.GameType);

        OpenCommand = ReactiveCommand.Create(() => OpenPage(preferInstall: false));
        InstallCommand = ReactiveCommand.Create(() => OpenPage(preferInstall: true));
        RemoveCommand = ReactiveCommand.CreateFromTask(() =>
            new WabbajackModlistRemover(_provider).ConfirmAndRemove(_windowManager, _windowManager.ActiveWorkspaceController, File));

        var install = WabbajackModlistInstallState.ReadInstall(_connection.Db, File, _loadout);
        State = install.State;
        SupersededBy = install.SupersededBy;
        OtherVersion = install.OtherVersion;
        // The list is read in the background (see Refresh); until then every held archive counts, as it does with no list to check.
        ArchivesHad = CountHeld(_connection.Db, HeldArchiveCheck.NothingNeeded);

        this.WhenActivated(d =>
        {
            Refresh();
            // Files arrive and installs finish while the tab is open; one scan per burst of revisions.
            _connection.Revisions
                .ThrottleAfterFirst(RevisionThrottle, TaskPoolScheduler.Default)
                .OnUI()
                .Subscribe(_ => Refresh())
                .DisposeWith(d);
        });
    }

    public LibraryFileId File { get; }
    public string Name { get; }
    public string Author { get; }
    public string Version { get; }
    public string GameName { get; }
    public string Source { get; }
    public string Kind => "Wabbajack modlist";
    public Size ArchiveSize { get; }
    public Size InstallSize { get; }
    public int ArchivesNeeded { get; }

    [Reactive] public partial int ArchivesHad { get; private set; }
    [Reactive] public partial WabbajackModlistCardState State { get; private set; }
    [Reactive] public partial string? SupersededBy { get; private set; }
    [Reactive] public partial string? OtherVersion { get; private set; }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    private static string GameNameFor(IServiceProvider provider, string gameType)
    {
        if (WabbajackGames.GameIdFor(gameType) is not { } gameId) return "Unknown game";
        var game = provider.GetServices<IGameData>().FirstOrDefault(g => g.GameId == gameId);
        return game?.DisplayName ?? "Unknown game";
    }

    /// <summary>The scan walks every archive of the list, which takes a moment for a big one, so it stays off the UI thread.</summary>
    private void Refresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var check = await ReadHeldCheck();
                var db = _connection.Db;
                var install = WabbajackModlistInstallState.ReadInstall(db, File, _loadout);
                var had = CountHeld(db, check);
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    State = install.State;
                    SupersededBy = install.SupersededBy;
                    OtherVersion = install.OtherVersion;
                    ArchivesHad = had;
                });
            }
            catch (Exception e)
            {
                // A revision can land while the list is being removed; the next one corrects the card.
                _provider.GetService<Microsoft.Extensions.Logging.ILogger<WabbajackModlistCardViewModel>>()?.LogDebug(e, "Could not refresh the card of '{List}'", Name);
            }
        });
    }

    /// <summary>
    /// A copy that lacks the nested archives the list copies whole is not held, so the count needs the list's directives.
    /// A list that cannot be read counts every copy, so the card still shows what the library has.
    /// </summary>
    private async Task<HeldArchiveCheck> ReadHeldCheck()
    {
        try
        {
            return await _provider.GetRequiredService<IModlistPlanner>().ReadHeldCheck(File, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _provider.GetService<Microsoft.Extensions.Logging.ILogger<WabbajackModlistCardViewModel>>()?.LogDebug(e, "Could not read '{List}' to check its held archives", Name);
            return HeldArchiveCheck.NothingNeeded;
        }
    }

    private int CountHeld(IDb db, HeldArchiveCheck check)
        => WabbajackModlistArchive.FindByModlist(db, File.Value).Count(row => check.IsHeld(db, _locator, row.Identity));

    private void OpenPage(bool preferInstall)
    {
        var page = new PageData
        {
            FactoryId = WabbajackModlistPageFactory.StaticId,
            Context = new WabbajackModlistPageContext
            {
                LibraryFileId = File,
                TargetLoadout = preferInstall ? _loadout : null,
            },
        };
        var workspaceController = _windowManager.ActiveWorkspaceController;
        workspaceController.OpenPage(_workspaceId, page, workspaceController.GetOpenPageBehavior(page, new NavigationInformation(NavigationInput.Default, Optional<OpenPageBehaviorType>.None)));
    }
}
