using SilverModManager.Sdk.Hashes;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using ReactiveUI;

namespace SilverModManager.UI.Tests.Pages;

public partial class WabbajackModlistViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LibraryFileId SomeFile = LibraryFileId.From(EntityId.From(0xCAFE));

    private sealed class FakeAcquirer : IArchiveAcquirer
    {
        public ArchivePlan Plan { get; private set; } = new([]);
        public event Action<ArchivePlan>? PlanChanged;
        public event Action? PlanForgotten { add { } remove { } }
        public AcquireStatus? Status { get; set; }
        public event Action<AcquireStatus>? StatusChanged;
        public int Cancels { get; private set; }
        public void Cancel() => Cancels++;
        public void ChangeStatus(AcquireStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(status);
        }
        public List<int> OpenNextCalls { get; } = [];
        public int Releases { get; private set; }
        public int Runs { get; private set; }
        public List<ArchivePlan> Shown { get; } = [];
        public ArchivePlan? LastRunPlan { get; private set; }

        /// <summary>When set, the next run stays unfinished until it is completed, like a download that is under way.</summary>
        public TaskCompletionSource? RunGate { get; set; }
        public CancellationToken LastRunToken { get; private set; }

        /// <summary>Like the real acquirer when a plan is shown while no page is active: it keeps no rows.</summary>
        public bool KeepsNothingShown { get; set; }

        public void Show(ArchivePlan plan)
        {
            Plan = KeepsNothingShown ? new([]) : plan;
            Shown.Add(plan);
        }

        public Task Run(ArchivePlan plan, IProgress<AcquireProgress>? progress, CancellationToken cancellationToken)
        {
            Plan = plan;
            LastRunPlan = plan;
            Runs++;
            LastRunToken = cancellationToken;
            return RunGate?.Task ?? Task.CompletedTask;
        }

        public int OpenNext(int count)
        {
            OpenNextCalls.Add(count);
            return 0;
        }

        public int AttachedPages { get; private set; }

        public IDisposable Attach()
        {
            AttachedPages++;
            return System.Reactive.Disposables.Disposable.Create(() => AttachedPages--);
        }

        /// <summary>Like the real acquirer once no page, run or expected download needs the plan.</summary>
        public void Forget() => Plan = new([]);

        public void Recheck() { }
        public void Release() => Releases++;

        public void Change(ArchivePlan plan)
        {
            Plan = plan;
            PlanChanged?.Invoke(plan);
        }
    }

    private sealed class FakeRun : IWabbajackInstallRun
    {
        public Subject<ModlistInstallProgress.Snapshot> Snapshots { get; } = new();
        public TaskCompletionSource Done { get; } = new();
        public IObservable<ModlistInstallProgress.Snapshot> Progress => Snapshots;
        public Task Completion => Done.Task;
    }

    private sealed class FakeService(GameId? game, Func<Task<SilverModManager.Sdk.Loadouts.LoadoutId>>? createLoadout = null) : IWabbajackModlistService
    {
        public int LoadoutsCreated { get; private set; }
        public List<SilverModManager.Sdk.Loadouts.LoadoutId?> PlannedFor { get; } = [];

        public ArchivePlan Plan { get; set; } = new([]);
        public Size? Free { get; set; }
        public IReadOnlyList<RelativePath> GameFiles { get; set; } = [];
        public FakeRun Run { get; } = new();
        public List<InstallOptions> Installs { get; } = [];
        public List<SilverModManager.Sdk.Loadouts.LoadoutId> InstalledInto { get; } = [];
        public IReadOnlyList<UnmatchedInbox> Unmatched { get; set; } = [];

        /// <summary>Whether the list was read on the UI thread, where parsing a big one would freeze the page.</summary>
        public bool? OpenedOnUiThread { get; private set; }

        public Task<WabbajackModlistDetails> Open(LibraryFileId file, CancellationToken cancellationToken)
        {
            OpenedOnUiThread = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            return Task.FromResult(new WabbajackModlistDetails("Test list", "Someone", "1.2.3", "SkyrimSpecialEdition", game, 2, Size.GB * 5, Size.GB * 9, Banner: null));
        }

        /// <summary>When set, the next plan waits for it, like a list whose game files take a while to hash.</summary>
        public TaskCompletionSource? PlanGate { get; set; }

        /// <summary>Completes once a plan has been recorded and is waiting at the gate. The page starts a check on the UI thread but the fake records it on the task pool, so "the page is planning" does not yet mean "the plan was recorded".</summary>
        public TaskCompletionSource PlanReachedGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, planning fails with it, like a list whose game is not installed.</summary>
        public Exception? PlanFailure { get; set; }

        /// <summary>What the page handed the last plan to report its progress through.</summary>
        public IProgress<PlanProgress>? LastProgress { get; private set; }

        /// <summary>Whether the last plan was made on the UI thread, where a long one would freeze the page.</summary>
        public bool? PlannedOnUiThread { get; private set; }

        public WabbajackModlistBlurb Blurb { get; set; } = new("", null);

        public Task<WabbajackModlistBlurb> ReadBlurb(LibraryFileId file, CancellationToken cancellationToken) => Task.FromResult(Blurb);

        public async Task<ArchivePlan> BuildPlan(LibraryFileId file, SilverModManager.Sdk.Loadouts.LoadoutId? target, IProgress<PlanProgress>? progress, CancellationToken cancellationToken)
        {
            PlannedFor.Add(target);
            LastProgress = progress;
            PlannedOnUiThread = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            var plan = Plan;
            if (PlanGate is { } gate)
            {
                PlanGate = null;
                PlanReachedGate.TrySetResult();
                await gate.Task;
            }
            if (PlanFailure is { } failure) throw failure;
            return plan;
        }

        public Task<IReadOnlyList<RelativePath>> GameFilesReplaced(LibraryFileId file, SilverModManager.Sdk.Loadouts.LoadoutId? target, IProgress<PlanProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(GameFiles);
        public async Task<SilverModManager.Sdk.Loadouts.LoadoutId> CreateLoadout(GameId gameId, CancellationToken cancellationToken)
        {
            LoadoutsCreated++;
            return await (createLoadout ?? throw new NotSupportedException())();
        }

        public IWabbajackInstallRun StartInstall(LibraryFileId file, SilverModManager.Sdk.Loadouts.LoadoutId target, InstallOptions options)
        {
            Installs.Add(options);
            InstalledInto.Add(target);
            return Run;
        }

        public List<(LibraryFileId Archive, SilverModManager.Sdk.Loadouts.LoadoutId Target)> InstalledArchives { get; } = [];

        public Task InstallArchive(LibraryFileId archive, SilverModManager.Sdk.Loadouts.LoadoutId target, CancellationToken cancellationToken)
        {
            InstalledArchives.Add((archive, target));
            return Task.CompletedTask;
        }

        public Size? FreeSpace() => Free;
        public Task<IReadOnlyList<UnmatchedInbox>> CheckInbox(CancellationToken cancellationToken) => Task.FromResult(Unmatched);
        public void OpenInboxFolder() { }

        public bool PickUpFromDownloads { get; set; }
        public string DownloadsFolderText { get; set; } = "/home/test/Downloads";
        public bool IsWatchingDownloads { get; set; }
        public int FolderChoices { get; private set; }
        public int FolderResets { get; private set; }

        public Task ChooseDownloadsFolder(CancellationToken cancellationToken)
        {
            FolderChoices++;
            DownloadsFolderText = "/mnt/chosen";
            return Task.CompletedTask;
        }

        public void UseDefaultDownloadsFolder()
        {
            FolderResets++;
            DownloadsFolderText = "/home/test/Downloads";
        }
        public Task<IReadOnlyList<UnmatchedInbox>> AddToInbox(IReadOnlyList<AbsolutePath> files, CancellationToken cancellationToken) => Task.FromResult(Unmatched);
    }

    private static ArchivePlanRow Row(ArchiveState state, string name = "a.7z", long size = 1_000_000_000)
        => new(new ArchiveEntry(FileIdentity.XxHash64(Hash.From((ulong)name.GetHashCode())), size, name, "", new ManualState(new Uri("https://example.com/a"), "get it")), state, null, null, null);

    private static ArchivePlan Plan(params ArchivePlanRow[] rows) => new(rows);

    private GameId TestGame => Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First().Game.GameId;

    private async Task<(WabbajackModlistViewModel Vm, FakeService Service, FakeAcquirer Acquirer)> Open(ArchivePlan plan, GameId? game = null, Size? free = null, bool createsLoadouts = false, bool load = true, bool acquirerKeepsNothingShown = false, bool pickUp = false, IServiceProvider? services = null, AcquireStatus? runUnderWay = null)
    {
        await SeedLoadoutRow(GameStore.Steam, "Wabbajack target");
        var service = new FakeService(game ?? TestGame, createsLoadouts ? async () => (await SeedLoadoutRow(GameStore.Steam, "Created by install")).LoadoutId : null) { Plan = plan, Free = free, PickUpFromDownloads = pickUp };
        var acquirer = new FakeAcquirer { KeepsNothingShown = acquirerKeepsNothingShown, Status = runUnderWay };
        var vm = new WabbajackModlistViewModel(Provider.GetRequiredService<IWindowManager>(), Connection, service, acquirer, services ?? Provider) { FileId = SomeFile };
        if (load) await vm.LoadAsync(CancellationToken.None);
        return (vm, service, acquirer);
    }

    [Fact]
    public async Task Install_IsDisabledWhileARowIsManual_AndEnabledOnceEveryRowIsHeld()
    {
        var manual = Row(ArchiveState.Manual);
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Have, "held.7z"), manual));

        // The page listens to the acquirer only while it is on screen.
        using var activation = vm.Activator.Activate();
        vm.SelectedLoadout.Should().NotBeNull();
        vm.CanInstall.Should().BeFalse();

        acquirer.Change(Plan(Row(ArchiveState.Have, "held.7z"), manual with { State = ArchiveState.Have }));

        vm.CanInstall.Should().BeTrue();
    }

    [Fact]
    public async Task Install_GameFilesCountAsHeld()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have, "held.7z"), Row(ArchiveState.GameFile, "Skyrim.exe")));

        vm.CanInstall.Should().BeTrue();
    }

    [Fact]
    public async Task Install_IsDisabledForAGameFileOfAnotherVersion()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.GameFileVersion, "Skyrim.exe")));

        vm.CanInstall.Should().BeFalse();
    }

    [Fact]
    public async Task TickingSkipMissingArchives_EnablesInstallAndTheOptionIsPassedOn()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Manual)));
        vm.CanInstall.Should().BeFalse();

        vm.SkipMissingArchives = true;
        vm.CanInstall.Should().BeTrue();

        vm.InstallCommand.Execute().Subscribe();
        service.Installs.Should().ContainSingle().Which.SkipMissingArchives.Should().BeTrue();
        service.Run.Done.SetResult();
    }

    [Fact]
    public async Task OpenNextFive_AsksTheAcquirerForFive()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Browser)));
        await vm.DownloadCommand.Execute();

        await vm.OpenNextFiveCommand.Execute();

        acquirer.OpenNextCalls.Should().Equal(5);
    }

    [Fact]
    public async Task TheLoadoutPicker_ListsTheLoadoutsOfTheListsGameAndOffersANewOne()
    {
        var (vm, _, _) = await Open(Plan());

        vm.LoadoutChoices.Should().Contain(choice => choice.Name == "Wabbajack target");
        vm.LoadoutChoices.Last().Id.Should().BeNull("the last entry creates a loadout");
    }

    [Fact]
    public async Task TheLoadoutPicker_HasNoLoadoutsForAGameNoLoadoutBelongsTo()
    {
        var (vm, _, _) = await Open(Plan(), game: GameId.From("CreationEngine.NoSuchGame"));

        vm.LoadoutChoices.Should().ContainSingle().Which.Id.Should().BeNull();
    }

    [Fact]
    public async Task OpeningAListWithoutLoadouts_SelectsCreateNewSoInstallIsNotWaitingForAPick()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), game: GameId.From("CreationEngine.NoSuchGame"));

        vm.SelectedLoadout.Should().Be(WabbajackLoadoutChoice.CreateNew);
        service.PlannedFor.Should().ContainSingle().Which.Should().BeNull("the plan is made against the game while the loadout does not exist");
        vm.CanInstall.Should().BeTrue();
        vm.InstallBlockedReason.Should().BeEmpty();
        service.LoadoutsCreated.Should().Be(0);
    }

    [Fact]
    public async Task TheProgressText_FollowsThePhasesOfTheRun()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        vm.InstallCommand.Execute().Subscribe();

        service.Run.Snapshots.OnNext(ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Check });
        vm.ProgressText.Should().Be("Checking the list");

        service.Run.Snapshots.OnNext(ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = 12, TotalItems = 40 });
        vm.ProgressText.Should().Be("Writing files: 12 of 40");
        vm.ProgressFraction.Should().BeApproximately(0.3, 1e-9);

        service.Run.Snapshots.OnNext(ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Order });
        vm.ProgressText.Should().Be("Ordering mods and plugins");
        service.Run.Done.SetResult();
    }

    [Fact]
    public async Task TheFreeSpaceText_WarnsBelowTheEstimate_AndSaysAtLeastWhileAnArchiveIsMissing()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual, size: 20_000_000_000)), free: Size.GB * 2);

        vm.IsFreeSpaceWarning.Should().BeTrue();
        vm.FreeSpaceText.Should().Contain("Not enough free space").And.Contain("at least");
    }

    [Fact]
    public async Task TheFreeSpaceText_IsNotAWarningWhenThereIsRoom()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual, size: 2_000_000_000)), free: Size.GB * 200);

        vm.IsFreeSpaceWarning.Should().BeFalse();
        vm.FreeSpaceText.Should().Contain("at least");
    }

    [Fact]
    public async Task GameFilesTheListReplaces_FeedTheFilesTheUserKeeps()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        service.GameFiles = [RelativePath.FromUnsanitizedInput("SkyrimSE.exe"), RelativePath.FromUnsanitizedInput("d3d11.dll")];
        await vm.LoadAsync(CancellationToken.None);

        vm.GameFilesReplaced.Should().HaveCount(2);
        vm.GameFilesReplaced[1].Keep = true;
        vm.InstallCommand.Execute().Subscribe();

        service.Installs.Should().ContainSingle().Which.GameFilesNotToReplace.Should().BeEquivalentTo([RelativePath.FromUnsanitizedInput("d3d11.dll")]);
        service.Run.Done.SetResult();
    }

    [Fact]
    public async Task LeavingThePage_KeepsItsDownloadsAndTheChosenLoadout()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Browser)));
        var chosen = vm.SelectedLoadout;

        // Switching to another tab deactivates the page; the user may be going to watch the downloads.
        vm.Activator.Activate().Dispose();
        using var back = vm.Activator.Activate();

        acquirer.Releases.Should().Be(0, "the Nexus Mods files the page opened must still be accepted");
        acquirer.LastRunToken.IsCancellationRequested.Should().BeFalse();
        acquirer.Runs.Should().Be(0, "coming back must not start anything");
        acquirer.Shown.Should().HaveCount(1, "coming back must not plan the list again");
        vm.SelectedLoadout.Should().Be(chosen);
    }

    [Fact]
    public async Task ThePage_IsAttachedToTheAcquirerOnlyWhileItIsActive()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Manual)));
        acquirer.AttachedPages.Should().Be(0);

        var activation = vm.Activator.Activate();
        acquirer.AttachedPages.Should().Be(1);

        activation.Dispose();
        acquirer.AttachedPages.Should().Be(0);
    }

    [Fact]
    public async Task ComingBack_ShowsThePlanAgainWhenTheAcquirerForgotIt()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Manual, "mine.7z")));
        vm.Activator.Activate().Dispose();
        acquirer.Forget();

        using var back = vm.Activator.Activate();

        acquirer.Shown.Should().HaveCount(2);
        acquirer.Plan.Rows.Should().ContainSingle().Which.Archive.Name.Should().Be("mine.7z");
        vm.Archives.Should().ContainSingle().Which.Name.Should().Be("mine.7z");
    }

    [Fact]
    public async Task APlanTheAcquirerKeepsNothingOf_StillFillsThePage()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual, "mine.7z")), acquirerKeepsNothingShown: true);

        vm.Archives.Should().ContainSingle().Which.Name.Should().Be("mine.7z");
    }

    [Fact]
    public async Task APlanOfAnotherList_IsNotShown()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Manual, "mine.7z")));
        using var activation = vm.Activator.Activate();

        // Another modlist page made its own plan the acquirer's current one.
        acquirer.Change(Plan(Row(ArchiveState.Have, "theirs.7z")));

        vm.Archives.Should().ContainSingle().Which.Name.Should().Be("mine.7z");
    }

    [Fact]
    public async Task CheckInbox_NamesTheFilesThatMatchedNothing()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Manual)));
        service.Unmatched = [new UnmatchedInbox("wrong.zip", FileIdentity.XxHash64(Hash.From(1))), new UnmatchedInbox("other.7z", FileIdentity.XxHash64(Hash.From(2)))];

        await vm.CheckInboxCommand.Execute();

        vm.InboxText.Should().Contain("wrong.zip").And.Contain("other.7z");

        service.Unmatched = [];
        await vm.CheckInboxCommand.Execute();
        vm.InboxText.Should().BeEmpty();
    }

    private static WabbajackLoadoutChoice CreateNew(WabbajackModlistViewModel vm) => vm.LoadoutChoices.Single(choice => choice.Id is null);

    // The install runs on the task pool, so a fixed delay is not always long enough under a full parallel test run.
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
            await Task.Delay(25, cts.Token);
    }

    [Fact]
    public async Task OpeningThePage_ShowsThePlanAndStartsNothing()
    {
        var (vm, service, acquirer) = await Open(Plan(Row(ArchiveState.Automatic, "auto.7z"), Row(ArchiveState.Browser, "browser.7z")), createsLoadouts: true);

        vm.Archives.Should().HaveCount(2);
        acquirer.Shown.Should().ContainSingle();
        acquirer.Runs.Should().Be(0, "no download job starts before Download is pressed");
        acquirer.OpenNextCalls.Should().BeEmpty("no browser tab opens by itself");
        service.LoadoutsCreated.Should().Be(0);
    }

    [Fact]
    public async Task Download_IsDisabledWhenNothingCanBeDownloaded_AndEnabledForAnAutomaticRow()
    {
        var (held, _, _) = await Open(Plan(Row(ArchiveState.Have), Row(ArchiveState.Manual, "manual.7z")));
        held.CanDownload.Should().BeFalse();

        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have), Row(ArchiveState.Automatic, "auto.7z")));
        vm.CanDownload.Should().BeTrue();
    }

    [Fact]
    public async Task Download_RunsThePlanOnce_AndCanBePressedAgain()
    {
        var plan = Plan(Row(ArchiveState.Automatic, "auto.7z"), Row(ArchiveState.Automatic, "other.7z"), Row(ArchiveState.Manual, "manual.7z"));
        var (vm, service, acquirer) = await Open(plan);

        await vm.DownloadCommand.Execute();

        acquirer.Runs.Should().Be(1);
        acquirer.LastRunPlan!.Rows.Where(row => row.State == ArchiveState.Automatic).Select(row => row.Archive.Name).Should().Equal("auto.7z", "other.7z");

        // A failed download turns its row Manual; pressing again plans the list afresh and tries what is left.
        await vm.DownloadCommand.Execute();

        acquirer.Runs.Should().Be(2);
        service.PlannedFor.Should().HaveCount(3, "the page's own plan plus one per press");
    }

    [Fact]
    public async Task PickingAnotherLoadoutWhileDownloadIsPlanning_StartsNothingForTheOldPick()
    {
        var (vm, service, acquirer) = await Open(Plan(Row(ArchiveState.Automatic, "auto.7z")), createsLoadouts: true);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;
        vm.DownloadCommand.Execute().Subscribe();
        // The gate holds whichever plan reaches the service first. Download plans on a pool thread, and a pick
        // made before that thread starts cancels it, so the pick's own plan would take the gate and the pick
        // would wait for a release that only comes after it returns.
        await WaitUntil(() => service.PlanGate is null);

        await vm.SelectLoadoutCommand.Execute(CreateNew(vm));
        gate.SetResult();
        await Task.Delay(200);

        acquirer.Runs.Should().Be(0, "the plan Download was making belongs to a loadout that is no longer picked");
        vm.ErrorText.Should().BeEmpty();
        vm.SelectedLoadout.Should().Be(CreateNew(vm));
    }

    [Fact]
    public async Task ChoosingCreateNew_OnlyRecordsTheChoice()
    {
        var (vm, service, acquirer) = await Open(Plan(Row(ArchiveState.Have)), createsLoadouts: true);

        await vm.SelectLoadoutCommand.Execute(CreateNew(vm));

        vm.SelectedLoadout.Should().Be(CreateNew(vm));
        service.LoadoutsCreated.Should().Be(0, "cancelling must leave nothing behind");
        service.PlannedFor.Last().Should().BeNull("the loadout does not exist, so the plan is made against the game");
        acquirer.Runs.Should().Be(0);
        vm.CanInstall.Should().BeTrue();
    }

    [Fact]
    public async Task Install_CreatesThePickedNewLoadoutAndInstallsIntoIt()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), createsLoadouts: true);
        await vm.SelectLoadoutCommand.Execute(CreateNew(vm));

        vm.InstallCommand.Execute().Subscribe();
        await WaitUntil(() => service.InstalledInto.Count > 0
            && vm.LoadoutChoices.Any(choice => choice.Name == "Created by install"));

        service.LoadoutsCreated.Should().Be(1);
        var created = vm.LoadoutChoices.Single(choice => choice.Name == "Created by install");
        service.InstalledInto.Should().Equal(created.Id!.Value);
        vm.SelectedLoadout.Should().Be(created);
        service.Run.Done.SetResult();
    }

    [Fact]
    public async Task Install_StillNeedsItsArchives_WhenTheLoadoutIsNew()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Manual)), createsLoadouts: true);
        await vm.SelectLoadoutCommand.Execute(CreateNew(vm));

        vm.CanInstall.Should().BeFalse();
        service.LoadoutsCreated.Should().Be(0);
    }

    [Fact]
    public async Task WhileTheListIsBeingChecked_BothButtonsAreOffAndSayTheListIsBeingChecked()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;

        var loading = vm.LoadAsync(CancellationToken.None);
        await WaitUntil(() => vm.IsPlanning);

        vm.CanInstall.Should().BeFalse();
        vm.CanDownload.Should().BeFalse();
        vm.InstallBlockedReason.Should().Contain("Checking the list");
        vm.DownloadBlockedReason.Should().Contain("Checking the list");

        gate.SetResult();
        await loading;

        vm.IsPlanning.Should().BeFalse();
        vm.CanInstall.Should().BeTrue();
        vm.InstallBlockedReason.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheListCannotBeChecked_InstallIsOffAndPointsAtTheError()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        service.PlanFailure = new InvalidOperationException("The game is not installed, so there is nothing to create a loadout for");

        await vm.LoadAsync(CancellationToken.None);

        vm.ErrorText.Should().Contain("The game is not installed");
        vm.CanInstall.Should().BeFalse("an empty plan only means the check failed");
        vm.InstallBlockedReason.Should().Be("The list could not be checked.");
        vm.DownloadBlockedReason.Should().Contain("could not be checked");
    }

    [Fact]
    public async Task Install_NamesHowManyArchivesAreMissingAndWhatToDoAboutIt()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual, "a.7z"), Row(ArchiveState.Browser, "b.7z"), Row(ArchiveState.Have, "c.7z")));

        vm.CanInstall.Should().BeFalse();
        vm.InstallBlockedReason.Should().Contain("2 of 3 archives are not in your library yet")
            .And.Contain("Skip files from missing archives");

        vm.SkipMissingArchives = true;

        vm.InstallBlockedReason.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_SaysWhenGameFilesDifferFromTheListsVersion()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.GameFileVersion, "Skyrim.exe"), Row(ArchiveState.Have, "c.7z")));

        vm.InstallBlockedReason.Should().Contain("1 game file differs from the version the list was made for");
    }

    [Fact]
    public async Task Download_SaysWhenEveryArchiveIsAlreadyThere()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have, "a.7z"), Row(ArchiveState.GameFile, "Skyrim.exe")));

        vm.CanDownload.Should().BeFalse();
        vm.DownloadBlockedReason.Should().Contain("Every archive is already in your library");
    }

    [Fact]
    public async Task Download_SaysWhenTheMissingArchivesNeedYouAndNotTheApp()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual, "a.7z"), Row(ArchiveState.Unsupported, "b.7z"), Row(ArchiveState.Have, "c.7z")));

        vm.CanDownload.Should().BeFalse();
        vm.DownloadBlockedReason.Should().Contain("None of the 2 missing archives can be fetched by the app")
            .And.Contain("Open page");
    }

    [Fact]
    public async Task Load_ShowsTheListsDescriptionAndReadme_AndTheHeaderStats()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        service.Blurb = new WabbajackModlistBlurb("A big list.", new Uri("https://example.com/readme"));

        await vm.LoadAsync(CancellationToken.None);

        vm.Description.Should().Be("A big list.");
        vm.ReadmeUri.Should().Be(new Uri("https://example.com/readme"));
        vm.ArchiveCountText.Should().Be("2 archives");
        vm.ArchiveSizeText.Should().Be((Size.GB * 5).ToString());
    }

    [Fact]
    public async Task Status_SaysTheBlockingReasonOnce_AndThePlanSummaryWhenNothingBlocks()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Manual), Row(ArchiveState.Have, "b.7z")));
        vm.Status.Text.Should().Be(vm.InstallBlockedReason);

        vm.SkipMissingArchives = true;
        vm.Status.Text.Should().Be(vm.DownloadBlockedReason, "install is allowed, so the line falls to Download's reason");
    }


    [Fact]
    public async Task Download_WhileRunning_SaysDownloadsAreRunning_AndTheReasonGoesWhenTheyFinish()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Automatic)));
        acquirer.RunGate = new TaskCompletionSource();

        await vm.DownloadCommand.Execute();

        vm.CanDownload.Should().BeFalse();
        vm.DownloadBlockedReason.Should().Contain("Downloads are running");

        acquirer.RunGate.SetResult();
        await WaitUntil(() => vm.CanDownload);

        vm.DownloadBlockedReason.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenNextFive_ExplainsItNeedsDownloadFirst()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Browser)));

        vm.OpenNextFiveBlockedReason.Should().Contain("Press Download first");

        await vm.DownloadCommand.Execute();

        vm.OpenNextFiveBlockedReason.Should().BeEmpty();
    }

    [Fact]
    public async Task ClickingInstallAgainWhileItRuns_StartsNoSecondInstall_AndSaysWhy()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        System.Windows.Input.ICommand button = vm.InstallCommand;

        // A button runs its command only while the command says it can; this is that check, twice in a row.
        for (var click = 0; click < 3; click++)
            if (button.CanExecute(null)) button.Execute(null);
        await WaitUntil(() => service.Installs.Count > 0);

        service.Installs.Should().ContainSingle("the second and third click came while the first install was running");
        vm.IsInstalling.Should().BeTrue();
        vm.ProgressText.Should().NotBeEmpty("the user sees that something is happening");
        vm.InstallBlockedReason.Should().Contain("An install is running");
        service.Run.Done.SetResult();
    }

    [Fact]
    public async Task ComingBackToThePage_AfterAFailedCheck_DoesNotMakeInstallAvailable()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        service.PlanFailure = new InvalidOperationException("The game is not installed");
        await vm.LoadAsync(CancellationToken.None);

        vm.Activator.Activate().Dispose();
        using var back = vm.Activator.Activate();

        vm.CanInstall.Should().BeFalse("the empty plan is what a failed check leaves behind");
        vm.InstallBlockedReason.Should().Contain("could not be checked");
        vm.DownloadBlockedReason.Should().Contain("could not be checked");
    }

    [Fact]
    public async Task APlanChangeWhileAnotherPickIsChecked_AndThenFails_DoesNotMakeInstallAvailable()
    {
        var plan = Plan(Row(ArchiveState.Have));
        var (vm, service, acquirer) = await Open(plan, createsLoadouts: true);
        using var activation = vm.Activator.Activate();
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;
        service.PlanFailure = new InvalidOperationException("The game is not installed");

        vm.SelectLoadoutCommand.Execute(CreateNew(vm)).Subscribe();
        await WaitUntil(() => vm.IsPlanning);
        acquirer.Change(plan);
        gate.SetResult();
        await WaitUntil(() => !vm.IsPlanning);

        vm.CanInstall.Should().BeFalse("the old pick's plan is not the new pick's");
        vm.InstallBlockedReason.Should().Contain("could not be checked");
    }

    [Fact]
    public async Task Download_WhileItPlansTheListAgain_SaysSo()
    {
        var (vm, service, acquirer) = await Open(Plan(Row(ArchiveState.Automatic)));
        acquirer.RunGate = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;

        vm.DownloadCommand.Execute().Subscribe();
        await WaitUntil(() => service.PlannedFor.Count == 2);

        vm.CanDownload.Should().BeFalse();
        vm.DownloadBlockedReason.Should().NotBeEmpty("a button that is off says why");
        vm.DownloadBlockedReason.Should().Contain("Checking the list");

        gate.SetResult();
        await WaitUntil(() => acquirer.Runs == 1);
        vm.DownloadBlockedReason.Should().Contain("Downloads are running");
        acquirer.RunGate.SetResult();
    }

    [Fact]
    public async Task OpenNextFive_WhenDownloadIsOff_GivesDownloadsReasonInsteadOfPointingAtDownload()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have, "a.7z")));

        vm.OpenNextFiveBlockedReason.Should().NotBeEmpty();
        vm.OpenNextFiveBlockedReason.Should().NotContain("Press Download first");
        vm.OpenNextFiveBlockedReason.Should().Contain("Every archive is already in your library");
    }

    [Fact]
    public async Task OpenNextFive_WhenNoArchiveNeedsTheBrowser_SaysSo()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Automatic, "a.7z")));

        vm.CanDownload.Should().BeTrue();
        vm.OpenNextFiveBlockedReason.Should().Be("No archive needs your browser.");
    }

    [Fact]
    public async Task Install_WhenOnlyGameFilesDiffer_DoesNotTellYouToGetAnything()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.GameFileVersion, "Skyrim.exe"), Row(ArchiveState.Have, "c.7z")));

        vm.InstallBlockedReason.Should().NotContain("Get what is missing");
        vm.InstallBlockedReason.Should().Contain("Skip files from missing archives");
    }

    private static ArchivePlanRow Held(string name, LibraryFile.ReadOnly file) => Row(ArchiveState.Have, name) with { Library = file };

    private async Task<LibraryFile.ReadOnly> SeedHeldFile(string name)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        var result = await tx.Commit();
        return LibraryFile.Load(Connection.Db, result.Remap(file).Id);
    }

    [Fact]
    public async Task AHeldArchiveCanBeInstalledOnItsOwnIntoTheSelectedLoadout()
    {
        var file = await SeedHeldFile("held-install.7z");
        var (vm, service, _) = await Open(Plan(Held("held-install.7z", file), Row(ArchiveState.Manual, "manual.7z"), Row(ArchiveState.Browser, "browser.7z")));

        vm.Archives.Select(row => row.CanInstall).Should().Equal(true, false, false);

        await vm.InstallArchiveCommand.Execute(vm.Archives[0]).FirstAsync();

        service.InstalledArchives.Should().ContainSingle().Which.Should().Be((LibraryFileId.From(file.Id), vm.SelectedLoadout!.Id!.Value));
    }

    [Fact]
    public async Task APageOpenedForALoadoutStartsOnThatLoadout()
    {
        await SeedLoadoutRow(GameStore.Steam, "First loadout of the game");
        var wanted = await SeedLoadoutRow(GameStore.Steam, "The loadout the card was on");
        var service = new FakeService(TestGame) { Plan = Plan() };
        var vm = new WabbajackModlistViewModel(Provider.GetRequiredService<IWindowManager>(), Connection, service, new FakeAcquirer(), Provider)
        {
            FileId = SomeFile,
            PreferredLoadout = wanted.LoadoutId,
        };

        await vm.LoadAsync(CancellationToken.None);

        vm.SelectedLoadout!.Id.Should().Be(wanted.LoadoutId);
        service.PlannedFor.Should().Equal(wanted.LoadoutId);
    }

    [Fact]
    public async Task APlanOfAnotherListIsNotThisPagesEvenWithTheSameArchives()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Manual, "same.7z")));
        using var activation = vm.Activator.Activate();
        var held = await SeedHeldFile("same.7z");

        acquirer.Change(new ArchivePlan([Held("same.7z", held)], LibraryFileId.From(EntityId.From(0xBEEFBEEF))));

        vm.Archives.Should().ContainSingle().Which.Plan.State.Should().Be(ArchiveState.Manual);
    }
}
