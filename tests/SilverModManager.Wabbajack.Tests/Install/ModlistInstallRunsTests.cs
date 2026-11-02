using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using NSubstitute;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Modlists;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>Which modlist installs are running, and what happens to a second one into the same loadout.</summary>
public class ModlistInstallRunsTests(ITestOutputHelper helper) : InstallTestBase<ModlistInstallRunsTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private readonly CapturingLoggerProvider _logs = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<ILoggerProvider>(_logs);

    private async Task<WabbajackModlistLibraryFile.ReadOnly> AddList()
    {
        var list = new ListBuilder();
        var exe = GameExeArchive();
        list.Archives.Add(exe);
        list.Directives.AddRange(
        [
            list.Inline("mods/B/b.txt", "b"),
            list.Inline("profiles/Default/modlist.txt", "+B\n"),
            list.Remapped("ModOrganizer.ini", "[General]\nselected_profile=@ByteArray(Default)\n"),
            new FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(exe.XxHash64, [])),
        ]);
        return await AddModlist(list.ToList(), list.Blobs);
    }

    /// <summary>Begins a job that does nothing until the returned gate is released, as a running install does.</summary>
    private (IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> Task, TaskCompletionSource Gate) BeginHeld(InstallModlistJob job)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ServiceProvider.GetRequiredService<IJobMonitor>().Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(job, async _ =>
        {
            await gate.Task;
            return default;
        });
        return (task, gate);
    }

    private ModlistInstallRuns Runs => ServiceProvider.GetRequiredService<ModlistInstallRuns>();

    [Fact]
    public async Task ASecondInstallIntoTheLoadoutOfARunningOneIsRefusedInPlainWords()
    {
        var loadout = await CreateLoadout();
        var modlist = await AddList();
        var (running, gate) = BeginHeld(NewJob(loadout, modlist, Options));

        var refused = await Record.ExceptionAsync(async () => await InstallModlistJob.Create(ServiceProvider, loadout.LoadoutId, modlist, Options));

        refused.Should().BeAssignableTo<UserFacingException>().Which.Message.Should().Be("An install into this loadout is already running");
        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task AnotherModlistIntoTheSameLoadoutIsRefusedToo()
    {
        var loadout = await CreateLoadout();
        var (running, gate) = BeginHeld(NewJob(loadout, await AddList(), Options));
        var other = await AddList();

        var refused = await Record.ExceptionAsync(async () => await InstallModlistJob.Create(ServiceProvider, loadout.LoadoutId, other, Options));

        refused.Should().BeOfType<InstallAlreadyRunningException>();
        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task AnInstallIntoAnotherLoadoutIsNotRefused()
    {
        var busy = await CreateLoadout();
        var idle = await CreateLoadout();
        var (running, gate) = BeginHeld(NewJob(busy, await AddList(), Options));

        var act = () => Runs.EnsureNoneInto(idle.LoadoutId);

        act.Should().NotThrow();
        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task OnceTheRunningInstallEndsTheLoadoutTakesAnotherAndNoRunIsReported()
    {
        var loadout = await CreateLoadout();
        var modlist = await AddList();
        var (running, gate) = BeginHeld(NewJob(loadout, modlist, Options));
        gate.SetResult();
        await running;

        Runs.Find(LibraryFileId.From(modlist.Id), loadout.LoadoutId).Should().BeNull();
        var act = () => Runs.EnsureNoneInto(loadout.LoadoutId);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task TheRunningInstallOfAFileIntoALoadoutIsFoundWithItsProgressAndCompletion()
    {
        var loadout = await CreateLoadout();
        var modlist = await AddList();
        var job = NewJob(loadout, modlist, Options);
        var (running, gate) = BeginHeld(job);
        job.Progress.Enter(InstallPhase.Write, 10, 100);
        job.Progress.Advance(3, 30);

        var found = Runs.Find(LibraryFileId.From(modlist.Id), loadout.LoadoutId);

        found.Should().NotBeNull();
        (await found!.Progress.Changes.FirstAsync()).Should().Be(job.Progress.Current);
        found.Progress.Current.DoneItems.Should().Be(3);
        found.Completion.IsCompleted.Should().BeFalse();
        Runs.Find(LibraryFileId.From(modlist.Id), loadout.LoadoutId).Should().BeSameAs(found, "the same run is the same object, so a follower can tell it already follows it");
        gate.SetResult();
        await found.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await running;
    }

    [Fact]
    public async Task TheRunningInstallOfAFileIsFoundWhicheverLoadoutItWritesInto()
    {
        var loadout = await CreateLoadout();
        var modlist = await AddList();
        var (running, gate) = BeginHeld(NewJob(loadout, modlist, Options));
        var other = await AddList();

        var found = Runs.FindOfFile(LibraryFileId.From(modlist.Id));

        found.Should().NotBeNull();
        found!.Loadout.Should().Be(loadout.LoadoutId);
        found.Should().BeSameAs(Runs.Find(LibraryFileId.From(modlist.Id), loadout.LoadoutId));
        Runs.FindOfFile(LibraryFileId.From(other.Id)).Should().BeNull("another list's page is not shown this run");
        gate.SetResult();
        await running;
        Runs.FindOfFile(LibraryFileId.From(modlist.Id)).Should().BeNull("a finished run is not running");
    }

    [Fact]
    public async Task OnlyTheRunOfThatFileIsFoundForThatFile()
    {
        var loadout = await CreateLoadout();
        var (running, gate) = BeginHeld(NewJob(loadout, await AddList(), Options));
        var other = await AddList();

        Runs.Find(LibraryFileId.From(other.Id), loadout.LoadoutId).Should().BeNull();
        Runs.FindInto(loadout.LoadoutId).Should().NotBeNull();
        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task AnInstallIntoTheLoadoutIsReportedAsInstallingUntilItEnds_AndNotForAnotherLoadout()
    {
        var busy = await CreateLoadout();
        var idle = await CreateLoadout();
        var (running, gate) = BeginHeld(NewJob(busy, await AddList(), Options));
        var seen = new List<bool>();
        using var _ = Runs.ObserveInstallingInto(busy.LoadoutId).Subscribe(seen.Add);

        Runs.IsInstallingInto(busy.LoadoutId).Should().BeTrue();
        Runs.IsInstallingInto(idle.LoadoutId).Should().BeFalse();
        gate.SetResult();
        await running;

        Runs.IsInstallingInto(busy.LoadoutId).Should().BeFalse();
        seen.Should().Equal(true, false);
    }

    [Fact]
    public async Task AnInstallThatIsCreatedButNotYetRunningIsInstallingForBothTheCheckAndTheObservable()
    {
        var loadout = await CreateLoadout();
        var job = NewJob(loadout, await AddList(), Options);
        var status = new BehaviorSubject<JobStatus>(JobStatus.Created);
        var handle = Substitute.For<IJob>();
        handle.Id.Returns(JobId.From(Guid.NewGuid()));
        handle.Definition.Returns(job);
        handle.Status.Returns(_ => status.Value);
        handle.ObservableStatus.Returns(status);
        var jobs = new SourceCache<IJob, JobId>(j => j.Id);
        var monitor = Substitute.For<IJobMonitor>();
        monitor.Snapshot().Returns(_ => jobs.Items.ToArray());
        monitor.GetObservableChangeSet<InstallModlistJob>().Returns(_ => jobs.Connect());
        jobs.AddOrUpdate(handle);
        var runs = new ModlistInstallRuns(monitor);
        var seen = new List<bool>();
        using var _ = runs.ObserveInstallingInto(loadout.LoadoutId).Subscribe(seen.Add);

        runs.IsInstallingInto(loadout.LoadoutId).Should().BeTrue();
        // Any other install starting re-evaluates the set, while ours has still not left Created.
        var otherJob = NewJob(await CreateLoadout(), await AddList(), Options);
        var other = Substitute.For<IJob>();
        other.Id.Returns(JobId.From(Guid.NewGuid()));
        other.Definition.Returns(otherJob);
        other.ObservableStatus.Returns(new BehaviorSubject<JobStatus>(JobStatus.Running));
        jobs.AddOrUpdate(other);
        seen.Should().Equal(true);
        status.OnNext(JobStatus.Running);
        status.OnNext(JobStatus.Completed);

        seen.Should().Equal(true, false);
    }

    [Fact]
    public async Task AFailedOrCancelledInstallIsNoLongerReportedAsInstalling()
    {
        var failed = await CreateLoadout();
        var cancelled = await CreateLoadout();
        var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();
        var modlist = await AddList();

        var failing = monitor.Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(NewJob(failed, modlist, Options),
            _ => throw new InvalidOperationException("boom"));
        await Record.ExceptionAsync(async () => await failing);
        var cancelGate = new TaskCompletionSource();
        var cancelling = monitor.Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(NewJob(cancelled, modlist, Options),
            async ctx =>
            {
                await cancelGate.Task.WaitAsync(ctx.CancellationToken);
                return default;
            });
        Runs.IsInstallingInto(cancelled.LoadoutId).Should().BeTrue();
        monitor.Cancel(cancelling);
        await Record.ExceptionAsync(async () => await cancelling);

        Runs.IsInstallingInto(failed.LoadoutId).Should().BeFalse();
        Runs.IsInstallingInto(cancelled.LoadoutId).Should().BeFalse();
    }

    [Fact]
    public async Task AnInstallLogsWhereItIsWithTheListName()
    {
        var loadout = await CreateLoadout();
        var modlist = await AddList();

        await Install(loadout, modlist, Options);

        _logs.Messages.Should().Contain(m => m.StartsWith($"Install of {modlist.Name}: Writing files "));
        _logs.Messages.Should().Contain($"Install of {modlist.Name}: Ordering mods and plugins");
    }
}

public class InstallProgressLogTests
{
    private static ModlistInstallProgress.Snapshot Snap(InstallPhase phase, int done, int total)
        => ModlistInstallProgress.Snapshot.Empty with { Phase = phase, DoneItems = done, TotalItems = total };

    private static (InstallProgressLog Log, CapturingLoggerProvider Messages) Create()
    {
        var provider = new CapturingLoggerProvider();
        return (new InstallProgressLog(provider.CreateLogger("test"), "Example List"), provider);
    }

    [Fact]
    public void APhaseWithoutItemsIsLoggedOnceWhenItStarts()
    {
        var (log, logged) = Create();

        log.Report(Snap(InstallPhase.Check, 0, 0));
        log.Report(Snap(InstallPhase.Check, 0, 0));
        log.Report(Snap(InstallPhase.Order, 0, 0));

        logged.Messages.Should().Equal("Install of Example List: Checking the list", "Install of Example List: Ordering mods and plugins");
    }

    [Fact]
    public void APhaseWithItemsIsLoggedWhenItStartsAndEachTimeItPassesAnotherFivePercent()
    {
        var (log, logged) = Create();

        log.Report(Snap(InstallPhase.Write, 0, 200));
        log.Report(Snap(InstallPhase.Write, 1, 200));
        log.Report(Snap(InstallPhase.Write, 9, 200));
        log.Report(Snap(InstallPhase.Write, 10, 200));
        log.Report(Snap(InstallPhase.Write, 11, 200));
        log.Report(Snap(InstallPhase.Write, 28, 200));
        log.Report(Snap(InstallPhase.Write, 29, 200));

        logged.Messages.Should().Equal(
            "Install of Example List: Writing files 0 of 200 (0%)",
            "Install of Example List: Writing files 10 of 200 (5%)",
            "Install of Example List: Writing files 28 of 200 (14%)");
    }

    [Fact]
    public void ThousandsAreSeparatedAndAJumpOverSeveralStepsIsOneLine()
    {
        var (log, logged) = Create();

        log.Report(Snap(InstallPhase.Write, 0, 281_096));
        log.Report(Snap(InstallPhase.Write, 41_200, 281_096));

        logged.Messages.Should().Equal(
            "Install of Example List: Writing files 0 of 281,096 (0%)",
            "Install of Example List: Writing files 41,200 of 281,096 (14%)");
    }

    [Fact]
    public void ANewPhaseStartsItsStepsAgain()
    {
        var (log, logged) = Create();

        log.Report(Snap(InstallPhase.Resolve, 50, 100));
        log.Report(Snap(InstallPhase.Write, 0, 100));
        log.Report(Snap(InstallPhase.Write, 3, 100));

        logged.Messages.Should().Equal(
            "Install of Example List: Locating files in archives 50 of 100 (50%)",
            "Install of Example List: Writing files 0 of 100 (0%)");
    }
}
