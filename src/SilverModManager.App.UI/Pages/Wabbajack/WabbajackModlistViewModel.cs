using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Helpers;
using NexusMods.App.UI.Pages.Diagnostics;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Icons;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.App.UI.Pages.Wabbajack;

[UsedImplicitly]
public class WabbajackModlistViewModel : APageViewModel<IWabbajackModlistViewModel>, IWabbajackModlistViewModel
{
    private readonly IConnection _connection;
    private readonly IWabbajackModlistService _service;
    private readonly IArchiveAcquirer _acquirer;
    private readonly IServiceProvider _provider;
    private readonly ILogger<WabbajackModlistViewModel>? _logger;

    private static readonly TimeSpan RevisionThrottle = TimeSpan.FromMilliseconds(300);

    private WabbajackModlistDetails? _details;
    private ArchivePlan _plan = new([]);
    private AcquireProgress? _downloads;
    private CancellationTokenSource? _acquireCancellation;
    private WabbajackModlistLoadoutGroupId? _groupId;
    private bool _loadStarted;

    [Reactive] public LibraryFileId FileId { get; set; }

    [Reactive] public string Name { get; private set; } = "";
    [Reactive] public string Author { get; private set; } = "";
    [Reactive] public string Version { get; private set; } = "";
    [Reactive] public string GameName { get; private set; } = "";
    [Reactive] public string ArchiveSummary { get; private set; } = "";
    [Reactive] public Bitmap? Banner { get; private set; }

    [Reactive] public IReadOnlyList<WabbajackLoadoutChoice> LoadoutChoices { get; private set; } = [WabbajackLoadoutChoice.CreateNew];
    [Reactive] public WabbajackLoadoutChoice? SelectedLoadout { get; private set; }

    [Reactive] public IReadOnlyList<WabbajackArchiveRow> Archives { get; private set; } = [];
    [Reactive] public string AcquireText { get; private set; } = "";
    [Reactive] public string InboxText { get; private set; } = "";
    [Reactive] public string ExtractedSizeText { get; private set; } = "";
    [Reactive] public string FreeSpaceText { get; private set; } = "";
    [Reactive] public bool IsFreeSpaceWarning { get; private set; }
    [Reactive] public IReadOnlyList<WabbajackGameFileChoice> GameFilesReplaced { get; private set; } = [];

    [Reactive] public bool SkipMissingArchives { get; set; }
    [Reactive] public bool CanInstall { get; private set; }
    [Reactive] public bool CanDownload { get; private set; }
    [Reactive] public bool IsDownloading { get; private set; }
    [Reactive] public bool HasStartedDownloads { get; private set; }
    [Reactive] public bool IsInstalling { get; private set; }
    [Reactive] public string ProgressText { get; private set; } = "";
    [Reactive] public double ProgressFraction { get; private set; }
    [Reactive] public string ErrorText { get; private set; } = "";
    [Reactive] public bool CanResume { get; private set; }
    [Reactive] public bool IsInstalled { get; private set; }
    [Reactive] public IReadOnlyList<WabbajackSkippedRow> SkippedFiles { get; private set; } = [];

    public ReactiveCommand<WabbajackLoadoutChoice, Unit> SelectLoadoutCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<WabbajackArchiveRow, Unit> OpenPageCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenNextFiveCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckInboxCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenInboxFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenHealthCheckCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public WabbajackModlistViewModel(
        IWindowManager windowManager,
        IConnection connection,
        IWabbajackModlistService service,
        IArchiveAcquirer acquirer,
        IServiceProvider provider) : base(windowManager)
    {
        _connection = connection;
        _service = service;
        _acquirer = acquirer;
        _provider = provider;
        _logger = provider.GetService<ILogger<WabbajackModlistViewModel>>();

        TabIcon = IconValues.Download;
        TabTitle = "Modlist";

        // Any of these can change whether Install is allowed.
        this.WhenAnyValue(vm => vm.SkipMissingArchives).Subscribe(_ => Recompute());

        SelectLoadoutCommand = ReactiveCommand.CreateFromTask<WabbajackLoadoutChoice>(async choice => await Guarded(token => SelectLoadoutAsync(choice, token)));
        InstallCommand = ReactiveCommand.CreateFromTask(InstallAsync, this.WhenAnyValue(vm => vm.CanInstall));
        OpenPageCommand = ReactiveCommand.Create<WabbajackArchiveRow>(row =>
        {
            if (row.Plan.Page is { } page) _provider.GetRequiredService<IOSInterop>().OpenUri(page);
        });
        // Not while a pick is still planning: its plan would replace the one Download starts on.
        DownloadCommand = ReactiveCommand.CreateFromTask(
            async () => await Guarded(DownloadAsync),
            this.WhenAnyValue(vm => vm.CanDownload).CombineLatest(SelectLoadoutCommand.IsExecuting, (can, picking) => can && !picking));
        // The nxm handler only expects the files once Download has run, so opening their pages before that would be refused.
        OpenNextFiveCommand = ReactiveCommand.Create(() => { _acquirer.OpenNext(5); }, this.WhenAnyValue(vm => vm.HasStartedDownloads));
        CheckInboxCommand = ReactiveCommand.CreateFromTask(async () => await Guarded(async token => ShowUnmatched(await _service.CheckInbox(token))));
        OpenInboxFolderCommand = ReactiveCommand.Create(() => _service.OpenInboxFolder());
        OpenHealthCheckCommand = ReactiveCommand.Create(OpenHealthCheck);
        RemoveCommand = ReactiveCommand.CreateFromTask(RemoveAsync, this.WhenAnyValue(vm => vm.IsInstalled, vm => vm.IsInstalling, (installed, installing) => installed && !installing));

        // Deactivation is not the end of the page: switching to another tab deactivates it, and the user may be
        // going to watch the downloads. So the archives keep coming, and the Nexus Mods files the page opened stay
        // expected, until the next run replaces this plan: another loadout here, or another modlist's page.
        this.WhenActivated(d =>
        {
            Observable.FromEvent<Action<ArchivePlan>, ArchivePlan>(h => h, h => _acquirer.PlanChanged += h, h => _acquirer.PlanChanged -= h)
                .Where(IsThisList)
                .Subscribe(ApplyPlan)
                .DisposeWith(d);
            // Rows that changed while the page was away.
            if (_loadStarted && IsThisList(_acquirer.Plan)) ApplyPlan(_acquirer.Plan);

            // The group, its enabled state and its skip rows change underneath the page while an install runs.
            _connection.Revisions
                .ThrottleAfterFirst(RevisionThrottle, TaskPoolScheduler.Default)
                .Subscribe(_ => RefreshInstalledState())
                .DisposeWith(d);

            if (!_loadStarted) _ = LoadAsync(CancellationToken.None);
        });
    }

    // The acquirer is one singleton for every modlist page; a plan of another list's archives is not this page's.
    private bool IsThisList(ArchivePlan plan)
        => plan.Rows.Select(row => row.Archive).SequenceEqual(_plan.Rows.Select(row => row.Archive));

    private void ShowUnmatched(IReadOnlyList<UnmatchedInbox> unmatched)
    {
        InboxText = unmatched.Count == 0
            ? ""
            : $"In the inbox but not part of this list: {string.Join(", ", unmatched.Select(file => file.FileName))}";
    }

    /// <summary>Reads the list, offers the loadouts of its game and builds the archive table for the first of them.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _loadStarted = true;
        await Guarded(async token =>
        {
            _details = await _service.Open(FileId, token);
            Name = _details.Name;
            Author = _details.Author;
            Version = _details.Version;
            GameName = _details.GameType;
            ArchiveSummary = $"{_details.ArchiveCount} archives, {_details.ArchiveSize}";
            Banner = _details.Banner;
            TabTitle = _details.Name;

            RefreshLoadoutChoices();
            if (LoadoutChoices.FirstOrDefault(choice => choice.Id is not null) is { } first)
                await SelectLoadoutAsync(first, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Picks the loadout to install into and builds the archive table against it. Nothing is downloaded and, for
    /// "Create new loadout", nothing is created: that waits for Download and Install.
    /// </summary>
    public async Task SelectLoadoutAsync(WabbajackLoadoutChoice choice, CancellationToken cancellationToken)
    {
        if (_details is null) throw new InvalidOperationException("The list was not opened yet");

        SelectedLoadout = choice;

        // Downloads for the previous pick are not wanted any more.
        _acquireCancellation?.Cancel();
        _acquireCancellation = new CancellationTokenSource();
        HasStartedDownloads = false;
        IsDownloading = false;

        var plan = await _service.BuildPlan(FileId, choice.Id, cancellationToken);
        GameFilesReplaced = (await _service.GameFilesReplaced(FileId, choice.Id, cancellationToken))
            .Select(path => new WabbajackGameFileChoice(path))
            .ToArray();

        // Show is what makes the plan the acquirer's current one: from here on it matches the rows to whatever
        // reaches the library, without fetching anything.
        _downloads = null;
        _acquirer.Show(plan);
        ApplyPlan(_acquirer.Plan);
        RefreshInstalledState();
    }

    /// <summary>
    /// Starts the downloads that need no one and expects the Nexus Mods files the user fetches in the browser. The
    /// list is planned again first, so a download that failed earlier is tried again.
    /// </summary>
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        ErrorText = "";
        _acquireCancellation?.Cancel();
        _acquireCancellation = new CancellationTokenSource();
        var token = _acquireCancellation.Token;

        ArchivePlan plan;
        try
        {
            plan = await _service.BuildPlan(FileId, SelectedLoadout?.Id, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        // Another loadout was picked while planning; its own plan is the one on the page now.
        if (token.IsCancellationRequested) return;

        _downloads = null;
        HasStartedDownloads = true;
        IsDownloading = true;
        var run = _acquirer.Run(plan, new Progress<AcquireProgress>(OnDownloadProgress), token);
        ApplyPlan(_acquirer.Plan);
        _ = ObserveDownloads(run, token);
    }

    private async Task ObserveDownloads(Task run, CancellationToken token)
    {
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "Getting the archives of '{List}' failed", Name);
            ErrorText = e.Message;
        }
        finally
        {
            // A run that a newer pick cancelled must not clear the flag of the run that replaced it.
            if (!token.IsCancellationRequested)
            {
                IsDownloading = false;
                Recompute();
            }
        }
    }

    private void OnDownloadProgress(AcquireProgress progress)
    {
        _downloads = progress;
        AcquireText = DescribePlan();
    }

    private void ApplyPlan(ArchivePlan plan)
    {
        _plan = plan;
        Archives = plan.Rows.Select(row => new WabbajackArchiveRow(row)).ToArray();

        var (estimate, isLowerBound) = WabbajackModlistText.ExtractedSize(plan);
        ExtractedSizeText = estimate.ToString();
        var note = WabbajackModlistText.FreeSpace(estimate, isLowerBound, _service.FreeSpace());
        FreeSpaceText = note.Text;
        IsFreeSpaceWarning = note.IsWarning;

        AcquireText = DescribePlan();
        Recompute();
    }

    private string DescribePlan()
    {
        if (_downloads is { Total: > 0 } downloads && downloads.Finished < downloads.Total)
            return WabbajackModlistText.Downloads(downloads);

        var missing = _plan.Rows.Count(row => !WabbajackModlistText.IsAvailable(row));
        return missing == 0 ? "Every archive is available" : $"{missing} of {_plan.Rows.Count} archives still to get";
    }

    private void Recompute()
    {
        CanInstall = SelectedLoadout is not null && !IsInstalling && WabbajackModlistText.CanInstall(_plan, SkipMissingArchives);
        CanDownload = !IsDownloading && _plan.Rows.Any(row => row.State is ArchiveState.Automatic or ArchiveState.Browser);
    }

    private void RefreshLoadoutChoices()
    {
        var game = _details?.Game;
        var choices = new List<WabbajackLoadoutChoice>();
        if (game is { } gameId)
        {
            var db = _connection.Db;
            var ids = WabbajackModlistText.LoadoutsFor(
                Loadout.All(db).Where(loadout => loadout.IsVisible() && GameInstallMetadata.Game.TryGetValue(loadout.Installation, out _))
                    .Select(loadout => (loadout.LoadoutId, GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var g) ? g : default)),
                gameId);
            choices.AddRange(ids.Select(id => new WabbajackLoadoutChoice(id, Loadout.Load(db, id).Name)));
        }

        choices.Add(WabbajackLoadoutChoice.CreateNew);
        LoadoutChoices = choices;
    }

    private void RefreshInstalledState()
    {
        try
        {
            var db = _connection.Db;
            WabbajackModlistLoadoutGroup.ReadOnly? found = null;
            if (SelectedLoadout?.Id is { } loadoutId)
            {
                found = WabbajackModlistLoadoutGroup.All(db)
                    .Where(group => group.ModlistId.Value == FileId.Value)
                    .Where(group => group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadoutId)
                    .Select(group => (WabbajackModlistLoadoutGroup.ReadOnly?)group)
                    .FirstOrDefault();
            }

            _groupId = found?.WabbajackModlistLoadoutGroupId;
            IsInstalled = found is not null;
            CanResume = found is { } group && !group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsEnabled();
            SkippedFiles = found is { } owner
                ? WabbajackSkippedFile.All(db).Where(skipped => skipped.GroupId == owner.WabbajackModlistLoadoutGroupId).Select(skipped => new WabbajackSkippedRow(skipped.To, skipped.Reason, skipped.Kind)).ToArray()
                : [];
        }
        catch (Exception e)
        {
            // A revision can land while the group is being removed; the next one corrects the state.
            _logger?.LogDebug(e, "Could not read the installed state of '{List}'", Name);
        }
    }

    private async Task InstallAsync()
    {
        var picked = SelectedLoadout ?? throw new InvalidOperationException("No loadout is selected");
        ErrorText = "";
        ProgressText = WabbajackModlistText.Progress(ModlistInstallProgress.Snapshot.Empty);
        ProgressFraction = 0;
        IsInstalling = true;
        Recompute();

        var keep = GameFilesReplaced.Where(file => file.Keep).Select(file => file.Path).ToHashSet();
        try
        {
            var target = picked.Id ?? await CreatePickedLoadout();
            var run = _service.StartInstall(FileId, target, new InstallOptions(SkipMissingArchives, keep));
            using var _ = run.Progress.Subscribe(snapshot =>
            {
                ProgressText = WabbajackModlistText.Progress(snapshot);
                ProgressFraction = WabbajackModlistText.Fraction(snapshot);
            });
            await run.Completion;
            ProgressText = "";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "";
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "Installing '{List}' failed", Name);
            ErrorText = e.Message;
            ProgressText = "";
        }
        finally
        {
            IsInstalling = false;
            RefreshInstalledState();
            Recompute();
        }
    }

    // "Create new loadout" is only a pick until Install; this is where the loadout comes into being.
    private async Task<LoadoutId> CreatePickedLoadout()
    {
        var game = _details?.Game ?? throw new InvalidOperationException($"The app has no title for the list's game ({_details?.GameType})");
        var created = await _service.CreateLoadout(game, CancellationToken.None);
        RefreshLoadoutChoices();
        SelectedLoadout = LoadoutChoices.First(choice => choice.Id == created);
        return created;
    }

    private void OpenHealthCheck()
    {
        var target = SelectedLoadout?.Id ?? throw new InvalidOperationException("No loadout is selected");
        var pageData = new PageData
        {
            FactoryId = DiagnosticListPageFactory.StaticId,
            Context = new DiagnosticListPageContext { LoadoutId = target },
        };
        GetWorkspaceController().OpenPage(WorkspaceId, pageData, new OpenPageBehavior.NewTab(PanelId));
    }

    private async Task RemoveAsync()
    {
        if (_groupId is not { } group) return;
        await Guarded(async _ => await CollectionDeleteHelpers.DeleteCollectionAsync(
            CollectionGroupId.From(group.Value),
            _provider.GetRequiredService<ILoadoutManager>(),
            GetWorkspaceController(),
            _connection,
            _provider.GetRequiredService<IWindowNotificationService>()));
        RefreshInstalledState();
    }

    public async Task AddDroppedFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var files = paths.Select(path => FileSystem.Shared.FromUnsanitizedFullPath(path)).Where(path => path.FileExists).ToArray();
        if (files.Length == 0) return;
        await Guarded(async token => ShowUnmatched(await _service.AddToInbox(files, token)), cancellationToken);
    }

    // Everything the page starts from a click ends up here, so a failure is shown on the page instead of being swallowed by the command.
    private Task Guarded(Func<CancellationToken, Task> action) => Guarded(action, CancellationToken.None);

    private async Task Guarded(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "The modlist page failed");
            ErrorText = e.Message;
        }
    }
}
