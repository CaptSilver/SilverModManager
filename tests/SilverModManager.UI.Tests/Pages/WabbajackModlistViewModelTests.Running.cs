using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>A batch the page did not start (the command line, the restart after a close) is shown, never abandoned.</summary>
public partial class WabbajackModlistViewModelTests
{
    private static AcquireStatus RunningFor(SilverModManager.Sdk.Library.LibraryFileId? list, int finished = 1, int total = 3, bool running = true)
        => new(list, running, finished, total, 0, TimeSpan.Zero, []);

    [Fact]
    public async Task OpeningTheListWhileItsBatchRuns_ShowsTheBatchInsteadOfReplacingIt()
    {
        var plan = Plan(Row(ArchiveState.Have, "held.7z"), Row(ArchiveState.Automatic, "next.7z"));
        var (vm, _, acquirer) = await Open(plan, runUnderWay: RunningFor(SomeFile));

        acquirer.Shown.Should().BeEmpty("showing a plan would abandon the running batch");
        vm.IsDownloading.Should().BeTrue();
        vm.HasStartedDownloads.Should().BeTrue();
        vm.Downloads.Should().Be(new AcquireProgress(1, 3));
        vm.CanDownload.Should().BeFalse("the batch is already going");
        vm.Archives.Should().HaveCount(2);
    }

    [Fact]
    public async Task ThePageFollowsAnOutsideBatchToItsEnd()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Automatic)), runUnderWay: RunningFor(SomeFile));
        using var activation = vm.Activator.Activate();

        acquirer.ChangeStatus(RunningFor(SomeFile, finished: 2));
        vm.Downloads.Should().Be(new AcquireProgress(2, 3));
        vm.IsDownloading.Should().BeTrue();

        acquirer.ChangeStatus(RunningFor(SomeFile, finished: 3, running: false));
        vm.Downloads.Should().Be(new AcquireProgress(3, 3));
        vm.IsDownloading.Should().BeFalse();
    }

    [Fact]
    public async Task ABatchOfAnotherListIsNotShownAsThisPagesButStillNotAbandoned()
    {
        var other = SilverModManager.Sdk.Library.LibraryFileId.From(SilverModManager.MnemonicDB.Abstractions.EntityId.From(0xBEEF));
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Automatic)), runUnderWay: RunningFor(other));

        acquirer.Shown.Should().BeEmpty();
        vm.IsDownloading.Should().BeFalse();
        vm.Archives.Should().HaveCount(1);
    }

    [Fact]
    public async Task APlanIsStillShownWhenTheOnlyRunIsOneThePageStartedAndThenReplaced()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Automatic)));
        acquirer.RunGate = new TaskCompletionSource();
        await vm.DownloadCommand.Execute();
        acquirer.Status = RunningFor(SomeFile);
        var shownBefore = acquirer.Shown.Count;

        await vm.SelectLoadoutAsync(vm.LoadoutChoices.First(c => c.Id is not null), CancellationToken.None);

        acquirer.Shown.Count.Should().BeGreaterThan(shownBefore, "picking again drops the page's own run and shows the new plan");
    }
}
