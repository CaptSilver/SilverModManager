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
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
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
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.Wabbajack;
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
        RemoveCommand = ReactiveCommand.CreateFromTask(RemoveAsync);

        State = WabbajackModlistInstallState.Read(_connection.Db, File, _loadout);
        ArchivesHad = CountHeld(_connection.Db);

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

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    // A list for a game this app has no title for stays visible in every library: hiding it would make the file
    // impossible to find and remove.
    internal static bool IsForGame(WabbajackModlistLibraryFile.ReadOnly file, GameId gameId)
        => WabbajackGames.GameIdFor(file.GameType) is not { } listGame || listGame == gameId;

    private static string GameNameFor(IServiceProvider provider, string gameType)
    {
        if (WabbajackGames.GameIdFor(gameType) is not { } gameId) return "Unknown game";
        var game = provider.GetServices<IGameData>().FirstOrDefault(g => g.GameId == gameId);
        return game?.DisplayName ?? "Unknown game";
    }

    /// <summary>The scan walks every archive of the list, which takes a moment for a big one, so it stays off the UI thread.</summary>
    private void Refresh()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var db = _connection.Db;
                var state = WabbajackModlistInstallState.Read(db, File, _loadout);
                var had = CountHeld(db);
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    State = state;
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

    private int CountHeld(IDb db)
    {
        var held = 0;
        foreach (var row in WabbajackModlistArchive.FindByModlist(db, File.Value))
        {
            if (_locator.FindByXxHash64(db, row.Identity) is not null) held++;
        }
        return held;
    }

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

    private static readonly ButtonDefinitionId RemoveWithArchives = ButtonDefinitionId.From("remove-with-archives");

    private async Task RemoveAsync()
    {
        var remover = new WabbajackModlistRemover(_provider);
        var plan = remover.Plan(File);

        var deleteArchives = false;
        var dialog = DialogFactory.CreateStandardDialog(
            title: $"Remove '{plan.Name}' from the library?",
            new StandardDialogParameters { Text = RemovalText(plan) },
            buttonDefinitions: RemovalButtons(plan));
        var result = await _windowManager.ShowDialog(dialog, DialogWindowType.Modal);
        if (result.ButtonId == RemoveWithArchives) deleteArchives = true;
        else if (result.ButtonId != ButtonDefinitionId.Accept) return;

        await remover.Remove(plan, deleteArchives, _windowManager.ActiveWorkspaceController);
    }

    internal static string RemovalText(ModlistRemovalPlan plan)
    {
        var text = new System.Text.StringBuilder();
        if (plan.LoadoutNames.Count > 0)
            text.AppendLine($"It is installed in {string.Join(", ", plan.LoadoutNames)}; those installs are removed too.");
        if (plan.OwnedArchives.Count > 0)
            text.AppendLine($"{plan.OwnedArchives.Count:N0} archives ({plan.OwnedSize}) were downloaded only for this modlist.");
        if (plan.Kept.Count > 0)
        {
            text.AppendLine("These stay in the library:");
            foreach (var kept in plan.Kept.Take(10)) text.AppendLine($"  {kept.Name}: {kept.Reason}");
            if (plan.Kept.Count > 10) text.AppendLine($"  and {plan.Kept.Count - 10:N0} more");
        }
        return text.Length == 0 ? "The modlist file is removed from the library." : text.ToString().TrimEnd();
    }

    private static DialogButtonDefinition[] RemovalButtons(ModlistRemovalPlan plan)
    {
        var keep = new DialogButtonDefinition(plan.OwnedArchives.Count > 0 ? "Remove list, keep archives" : "Remove", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Destructive);
        return plan.OwnedArchives.Count == 0
            ? [DialogStandardButtons.Cancel, keep]
            : [DialogStandardButtons.Cancel, keep, new DialogButtonDefinition($"Remove list and {plan.OwnedArchives.Count:N0} archives ({plan.OwnedSize})", RemoveWithArchives, ButtonAction.Accept, ButtonStyling.Destructive)];
    }
}
