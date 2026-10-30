using System.Diagnostics;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Wabbajack;

[UsedImplicitly]
public partial class WabbajackModlistViewModel : APageViewModel<IWabbajackModlistViewModel>, IWabbajackModlistViewModel
{
    private readonly IConnection _connection;
    private readonly IWabbajackModlistService _service;
    private readonly IArchiveAcquirer _acquirer;
    private readonly IServiceProvider _provider;
    private readonly ILogger<WabbajackModlistViewModel>? _logger;

    private static readonly TimeSpan RevisionThrottle = TimeSpan.FromMilliseconds(300);

    private WabbajackModlistDetails? _details;
    private ArchivePlan _plan = new([]);
    private CancellationTokenSource? _acquireCancellation;
    private WabbajackModlistLoadoutGroupId? _groupId;
    private bool _loadStarted;

    // The phase the log last named, so it says each once rather than at every count.
    private PlanPhase? _loggedPhase;

    // False from the moment a pick starts planning until its plan arrives, and after a plan that failed.
    // Only a pick that planned successfully sets it; the acquirer re-announcing a plan must not.
    private bool _planReady;

    // Download plans the list again before it starts, and the button is off for that time.
    private bool _isPreparingDownloads;

    [Reactive] public partial LibraryFileId FileId { get; set; }

    public LoadoutId? PreferredLoadout { get; set; }

    [Reactive] public partial string Name { get; private set; } = "";
    [Reactive] public partial string Author { get; private set; } = "";
    [Reactive] public partial string Version { get; private set; } = "";
    [Reactive] public partial string GameName { get; private set; } = "";
    [Reactive] public partial string ArchiveCountText { get; private set; } = "";
    [Reactive] public partial string ArchiveSizeText { get; private set; } = "";
    [Reactive] public partial string Description { get; private set; } = "";
    [Reactive] public partial Uri? ReadmeUri { get; private set; }
    [Reactive] public partial WabbajackStatus Status { get; private set; }
    [Reactive] public partial AcquireProgress? Downloads { get; private set; }
    [Reactive] public partial Bitmap? Banner { get; private set; }

    [Reactive] public partial IReadOnlyList<WabbajackLoadoutChoice> LoadoutChoices { get; private set; } = [WabbajackLoadoutChoice.CreateNew];
    [Reactive] public partial WabbajackLoadoutChoice? SelectedLoadout { get; private set; }

    [Reactive] public partial IReadOnlyList<WabbajackArchiveRow> Archives { get; private set; } = [];
    [Reactive] public partial string AcquireText { get; private set; } = "";
    [Reactive] public partial string InboxText { get; private set; } = "";
    [Reactive] public partial string ExtractedSizeText { get; private set; } = "";
    [Reactive] public partial string FreeSpaceText { get; private set; } = "";
    [Reactive] public partial bool IsFreeSpaceWarning { get; private set; }
    [Reactive] public partial IReadOnlyList<WabbajackGameFileChoice> GameFilesReplaced { get; private set; } = [];

    [Reactive] public partial bool SkipMissingArchives { get; set; }
    [Reactive] public partial bool CanInstall { get; private set; }
    [Reactive] public partial bool CanDownload { get; private set; }
    [Reactive] public partial string InstallBlockedReason { get; private set; } = "";
    [Reactive] public partial string DownloadBlockedReason { get; private set; } = "";
    [Reactive] public partial string OpenNextFiveBlockedReason { get; private set; } = "";
    [Reactive] public partial bool IsPlanning { get; private set; }
    [Reactive] public partial string CheckText { get; private set; } = "";
    [Reactive] public partial double CheckFraction { get; private set; }
    [Reactive] public partial bool IsCheckIndeterminate { get; private set; }
    [Reactive] public partial bool IsDownloading { get; private set; }
    [Reactive] public partial bool HasStartedDownloads { get; private set; }
    [Reactive] public partial bool IsInstalling { get; private set; }
    [Reactive] public partial string ProgressText { get; private set; } = "";
    [Reactive] public partial double ProgressFraction { get; private set; }
    [Reactive] public partial string ErrorText { get; private set; } = "";
    [Reactive] public partial bool CanResume { get; private set; }
    [Reactive] public partial bool IsInstalled { get; private set; }
    [Reactive] public partial IReadOnlyList<WabbajackSkippedRow> SkippedFiles { get; private set; } = [];

    public ReactiveCommand<WabbajackLoadoutChoice, Unit> SelectLoadoutCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; }
    public ReactiveCommand<WabbajackArchiveRow, Unit> OpenPageCommand { get; }
    public ReactiveCommand<WabbajackArchiveRow, Unit> InstallArchiveCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenNextFiveCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckInboxCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenInboxFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenHealthCheckCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenReadmeCommand { get; }

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
        this.WhenAnyValue(vm => vm.SkipMissingArchives, vm => vm.IsPlanning, vm => vm.IsDownloading, vm => vm.HasStartedDownloads)
            .Subscribe(_ => Recompute());

        // Ten inputs decide the one status line; a single subscription keeps them from being refreshed by hand at every setter.
        this.WhenAnyValue(
                vm => vm.ErrorText, vm => vm.ProgressText, vm => vm.ProgressFraction, vm => vm.CheckText, vm => vm.CheckFraction,
                vm => vm.IsCheckIndeterminate, vm => vm.Downloads, vm => vm.InstallBlockedReason, vm => vm.DownloadBlockedReason, vm => vm.AcquireText,
                (error, install, installFraction, check, checkFraction, indeterminate, downloads, installReason, downloadReason, plan) =>
                    WabbajackModlistText.Status(error, install, installFraction, check, checkFraction, indeterminate, downloads, installReason, downloadReason, plan))
            .Subscribe(status => Status = status);

        OpenReadmeCommand = ReactiveCommand.Create(
            () => { if (ReadmeUri is { } readme) _provider.GetRequiredService<IOSInterop>().OpenUri(readme); },
            this.WhenAnyValue(vm => vm.ReadmeUri).Select(uri => uri is not null));
        SelectLoadoutCommand = ReactiveCommand.CreateFromTask<WabbajackLoadoutChoice>(async choice => await Guarded(token => SelectLoadoutAsync(choice, token)));
        InstallCommand = ReactiveCommand.CreateFromTask(InstallAsync, this.WhenAnyValue(vm => vm.CanInstall));
        OpenPageCommand = ReactiveCommand.Create<WabbajackArchiveRow>(row =>
        {
            if (row.Plan.Page is { } page) _provider.GetRequiredService<IOSInterop>().OpenUri(page);
        });
        InstallArchiveCommand = ReactiveCommand.CreateFromTask<WabbajackArchiveRow>(async row =>
        {
            if (row.Plan.Library is not { } library || SelectedLoadout?.Id is not { } loadout) return;
            await Guarded(token => _service.InstallArchive(LibraryFileId.From(library.Id), loadout, token));
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
            // The acquirer matches the rows to the library only while a page like this one is active.
            _acquirer.Attach().DisposeWith(d);
            // It forgets a plan nobody shows, so a page that comes back to an empty one gives its plan back. Another
            // list's plan is left alone: its page or its downloads still need it.
            if (_planReady && _plan.Rows.Count > 0 && _acquirer.Plan.Rows.Count == 0) _acquirer.Show(_plan);

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
        => plan.Modlist is { } modlist
            ? modlist == FileId
            : plan.Rows.Select(row => row.Archive).SequenceEqual(_plan.Rows.Select(row => row.Archive));

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
        IsPlanning = true;
        // The list's name is not known yet, so this first line stays out of the log.
        ShowCheck(new PlanProgress(PlanPhase.ReadingList), log: false);
        // From the pick on, the pick itself ends the planning; ending it here too would end a newer pick's.
        var handedOver = false;
        try
        {
            await Guarded(async token =>
            {
                // Parsing the list and decoding its banner take seconds for a big list; on the UI thread the bar could not move.
                _details = await Task.Run(() => _service.Open(FileId, token), token);
                Name = _details.Name;
                Author = _details.Author;
                Version = _details.Version;
                GameName = _details.GameType;
                ArchiveCountText = $"{_details.ArchiveCount} archives";
                ArchiveSizeText = _details.ArchiveSize.ToString();
                Banner = _details.Banner;
                TabTitle = _details.Name;

                // Read after Open so the name and stats show before the list is parsed; the plan below reuses the parse.
                await ShowBlurb(token);

                RefreshLoadoutChoices();
                // Without a loadout of the game, "Create new loadout" is the only way forward, so it starts out picked.
                var first = LoadoutChoices.FirstOrDefault(choice => choice.Id is not null && choice.Id == PreferredLoadout)
                    ?? LoadoutChoices.FirstOrDefault(choice => choice.Id is not null)
                    ?? WabbajackLoadoutChoice.CreateNew;
                handedOver = true;
                await SelectLoadoutAsync(first, token);
            }, cancellationToken);
        }
        finally
        {
            if (!handedOver)
            {
                IsPlanning = false;
                ClearCheck();
            }
        }
    }

    // A list that cannot be read fails again in the check below, with the message the page shows; the header just goes without.
    private async Task ShowBlurb(CancellationToken token)
    {
        try
        {
            var blurb = await Task.Run(() => _service.ReadBlurb(FileId, token), token);
            Description = blurb.Description;
            ReadmeUri = blurb.Readme;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogDebug(e, "No description for '{List}'", Name);
        }
    }

    /// <summary>
    /// Picks the loadout to install into and builds the archive table against it. Nothing is downloaded and, for
    /// "Create new loadout", nothing is created: that waits for Download and Install. A pick made while an earlier
    /// one is still being checked replaces it: the earlier check is cancelled and its result is dropped.
    /// </summary>
    public async Task SelectLoadoutAsync(WabbajackLoadoutChoice choice, CancellationToken cancellationToken)
    {
        if (_details is null) throw new InvalidOperationException("The list was not opened yet");

        SelectedLoadout = choice;
        _planReady = false;
        IsPlanning = true;

        // Downloads for the previous pick are not wanted any more, and neither is its check.
        _acquireCancellation?.Cancel();
        var pick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acquireCancellation = pick;
        var token = pick.Token;
        HasStartedDownloads = false;
        IsDownloading = false;

        var started = Stopwatch.GetTimestamp();
        _loggedPhase = null;
        _logger?.LogInformation("Checking modlist '{List}' ({Archives} archives) against {Loadout}", Name, _details.ArchiveCount, choice.Name);
        ShowCheck(new PlanProgress(PlanPhase.ReadingList));
        try
        {
            var plan = await OffUi(progress => _service.BuildPlan(FileId, choice.Id, progress, token), token);
            var replaced = await OffUi(progress => _service.GameFilesReplaced(FileId, choice.Id, progress, token), token);
            if (token.IsCancellationRequested)
            {
                _logger?.LogInformation(
                    "Check of modlist '{List}' dropped: {Reason}", Name,
                    cancellationToken.IsCancellationRequested ? "the page was closed" : "another loadout was picked");
                return;
            }

            GameFilesReplaced = replaced.Select(path => new WabbajackGameFileChoice(path)).ToArray();

            // Show is what makes the plan the acquirer's current one: from here on it matches the rows to whatever
            // reaches the library, without fetching anything.
            Downloads = null;
            _acquirer.Show(plan);
            _planReady = true;
            // The acquirer keeps no rows when no page is active (the user left while planning); the page then
            // shows the plan as built, and the next activation gives it back and looks the rows up.
            var shown = _acquirer.Plan;
            ApplyPlan(shown.Rows.Count > 0 ? shown : plan);
            RefreshInstalledState();

            var counts = WabbajackModlistText.Counts(plan);
            _logger?.LogInformation(
                "Checked modlist '{List}' in {Seconds:0.0} s: {Ready} ready, {ToGet} to get, {ByHand} by hand, {Differing} game files differ, {NotNeeded} not needed",
                Name, Stopwatch.GetElapsedTime(started).TotalSeconds, counts.Ready, counts.ToGet, counts.ByHand, counts.GameFilesDiffer, counts.NotNeeded);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger?.LogInformation("Check of modlist '{List}' cancelled: another loadout was picked", Name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogWarning(e, "Checking modlist '{List}' failed after {Seconds:0.0} s: {Reason}", Name, Stopwatch.GetElapsedTime(started).TotalSeconds, e.Message);
            throw;
        }
        finally
        {
            // Only the newest pick owns the page's state; a replaced one finishing late must leave it alone.
            if (ReferenceEquals(_acquireCancellation, pick))
            {
                IsPlanning = false;
                ClearCheck();
                Recompute();
            }
        }
    }

    // Hashing and lookups over a big list take long enough to freeze the window when they run on the UI thread.
    private Task<T> OffUi<T>(Func<IProgress<PlanProgress>, Task<T>> work, CancellationToken token)
    {
        var progress = new InlineProgress<PlanProgress>(report =>
        {
            if (!token.IsCancellationRequested) ShowCheck(report);
        });
        return Task.Run(() => work(progress), token);
    }

    private void ShowCheck(PlanProgress progress, bool log = true)
    {
        CheckText = WabbajackModlistText.Checking(progress);
        CheckFraction = WabbajackModlistText.CheckFraction(progress);
        IsCheckIndeterminate = WabbajackModlistText.IsIndeterminate(progress);

        // Each phase is logged once, when it begins.
        if (!log || _loggedPhase == progress.Phase) return;
        _loggedPhase = progress.Phase;
        _logger?.LogInformation("Checking modlist '{List}': {Phase}", Name, CheckText);
    }

    private void ClearCheck()
    {
        CheckText = "";
        CheckFraction = 0;
        IsCheckIndeterminate = false;
    }

    // Reports go straight to the callback on the reporting thread. Progress<T> would post each to the captured
    // context, which is the UI thread this check is being kept off.
    private sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
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

        _isPreparingDownloads = true;
        Recompute();
        try
        {
            ArchivePlan plan;
            try
            {
                _loggedPhase = null;
                ShowCheck(new PlanProgress(PlanPhase.ReadingList));
                plan = await OffUi(progress => _service.BuildPlan(FileId, SelectedLoadout?.Id, progress, token), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            // Another loadout was picked while planning; its own plan is the one on the page now.
            if (token.IsCancellationRequested) return;

            Downloads = null;
            HasStartedDownloads = true;
            IsDownloading = true;
            var run = _acquirer.Run(plan, new Progress<AcquireProgress>(OnDownloadProgress), token);
            ApplyPlan(_acquirer.Plan);
            _ = ObserveDownloads(run, token);
        }
        finally
        {
            _isPreparingDownloads = false;
            if (!IsPlanning) ClearCheck();
            Recompute();
        }
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
        Downloads = progress;
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

        AcquireText = WabbajackModlistText.PlanSummary(plan);
        Recompute();
    }

    private void Recompute()
    {
        var hasLoadout = SelectedLoadout is not null;
        InstallBlockedReason = WabbajackModlistText.InstallBlockedReason(hasLoadout, IsPlanning, _planReady, IsInstalling, _plan, SkipMissingArchives);
        DownloadBlockedReason = WabbajackModlistText.DownloadBlockedReason(IsPlanning, _isPreparingDownloads, _planReady, IsDownloading, _plan);
        OpenNextFiveBlockedReason = WabbajackModlistText.OpenNextFiveBlockedReason(HasStartedDownloads, DownloadBlockedReason, _plan);

        // The reasons are the single source: a button is on exactly when it has none to give.
        CanInstall = InstallBlockedReason == "";
        CanDownload = DownloadBlockedReason == "";
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
