using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.App.UI.Pages.Wabbajack;
using NexusMods.App.UI.Windows;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;
using ReactiveUI;

namespace NexusMods.UI.Tests.Pages;

public class WabbajackModlistViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LibraryFileId SomeFile = LibraryFileId.From(EntityId.From(0xCAFE));

    private sealed class FakeAcquirer : IArchiveAcquirer
    {
        public ArchivePlan Plan { get; private set; } = new([]);
        public event Action<ArchivePlan>? PlanChanged;
        public List<int> OpenNextCalls { get; } = [];
        public int Releases { get; private set; }
        public int Runs { get; private set; }
        public List<ArchivePlan> Shown { get; } = [];
        public ArchivePlan? LastRunPlan { get; private set; }
        public CancellationToken LastRunToken { get; private set; }

        public void Show(ArchivePlan plan)
        {
            Plan = plan;
            Shown.Add(plan);
        }

        public Task Run(ArchivePlan plan, IProgress<AcquireProgress>? progress, CancellationToken cancellationToken)
        {
            Plan = plan;
            LastRunPlan = plan;
            Runs++;
            LastRunToken = cancellationToken;
            return Task.CompletedTask;
        }

        public int OpenNext(int count)
        {
            OpenNextCalls.Add(count);
            return 0;
        }

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

    private sealed class FakeService(GameId? game, Func<Task<NexusMods.Sdk.Loadouts.LoadoutId>>? createLoadout = null) : IWabbajackModlistService
    {
        public int LoadoutsCreated { get; private set; }
        public List<NexusMods.Sdk.Loadouts.LoadoutId?> PlannedFor { get; } = [];

        public ArchivePlan Plan { get; set; } = new([]);
        public Size? Free { get; set; }
        public IReadOnlyList<RelativePath> GameFiles { get; set; } = [];
        public FakeRun Run { get; } = new();
        public List<InstallOptions> Installs { get; } = [];
        public List<NexusMods.Sdk.Loadouts.LoadoutId> InstalledInto { get; } = [];
        public IReadOnlyList<UnmatchedInbox> Unmatched { get; set; } = [];

        public Task<WabbajackModlistDetails> Open(LibraryFileId file, CancellationToken cancellationToken)
            => Task.FromResult(new WabbajackModlistDetails("Test list", "Someone", "1.2.3", "SkyrimSpecialEdition", game, 2, Size.GB * 5, Size.GB * 9, Banner: null));

        /// <summary>When set, the next plan waits for it, like a list whose game files take a while to hash.</summary>
        public TaskCompletionSource? PlanGate { get; set; }

        public async Task<ArchivePlan> BuildPlan(LibraryFileId file, NexusMods.Sdk.Loadouts.LoadoutId? target, CancellationToken cancellationToken)
        {
            PlannedFor.Add(target);
            var plan = Plan;
            if (PlanGate is { } gate)
            {
                PlanGate = null;
                await gate.Task;
            }
            return plan;
        }

        public Task<IReadOnlyList<RelativePath>> GameFilesReplaced(LibraryFileId file, NexusMods.Sdk.Loadouts.LoadoutId? target, CancellationToken cancellationToken) => Task.FromResult(GameFiles);
        public async Task<NexusMods.Sdk.Loadouts.LoadoutId> CreateLoadout(GameId gameId, CancellationToken cancellationToken)
        {
            LoadoutsCreated++;
            return await (createLoadout ?? throw new NotSupportedException())();
        }

        public IWabbajackInstallRun StartInstall(LibraryFileId file, NexusMods.Sdk.Loadouts.LoadoutId target, InstallOptions options)
        {
            Installs.Add(options);
            InstalledInto.Add(target);
            return Run;
        }

        public Size? FreeSpace() => Free;
        public Task<IReadOnlyList<UnmatchedInbox>> CheckInbox(CancellationToken cancellationToken) => Task.FromResult(Unmatched);
        public void OpenInboxFolder() { }
        public Task<IReadOnlyList<UnmatchedInbox>> AddToInbox(IReadOnlyList<AbsolutePath> files, CancellationToken cancellationToken) => Task.FromResult(Unmatched);
    }

    private static ArchivePlanRow Row(ArchiveState state, string name = "a.7z", long size = 1_000_000_000)
        => new(new ArchiveEntry(Hash.From((ulong)name.GetHashCode()), size, name, "", new ManualState(new Uri("https://example.com/a"), "get it")), state, null, null, null);

    private static ArchivePlan Plan(params ArchivePlanRow[] rows) => new(rows);

    private GameId TestGame => Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First().Game.GameId;

    private async Task<(WabbajackModlistViewModel Vm, FakeService Service, FakeAcquirer Acquirer)> Open(ArchivePlan plan, GameId? game = null, Size? free = null, bool createsLoadouts = false)
    {
        await SeedLoadoutRow(GameStore.Steam, "Wabbajack target");
        var service = new FakeService(game ?? TestGame, createsLoadouts ? async () => (await SeedLoadoutRow(GameStore.Steam, "Created by install")).LoadoutId : null) { Plan = plan, Free = free };
        var acquirer = new FakeAcquirer();
        var vm = new WabbajackModlistViewModel(Provider.GetRequiredService<IWindowManager>(), Connection, service, acquirer, Provider) { FileId = SomeFile };
        await vm.LoadAsync(CancellationToken.None);
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
        vm.SelectedLoadout.Should().BeNull();
        vm.CanInstall.Should().BeFalse();
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
        service.Unmatched = [new UnmatchedInbox("wrong.zip", Hash.From(1)), new UnmatchedInbox("other.7z", Hash.From(2))];

        await vm.CheckInboxCommand.Execute();

        vm.InboxText.Should().Contain("wrong.zip").And.Contain("other.7z");

        service.Unmatched = [];
        await vm.CheckInboxCommand.Execute();
        vm.InboxText.Should().BeEmpty();
    }

    private static WabbajackLoadoutChoice CreateNew(WabbajackModlistViewModel vm) => vm.LoadoutChoices.Single(choice => choice.Id is null);

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
        await Task.Delay(200);

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
}
