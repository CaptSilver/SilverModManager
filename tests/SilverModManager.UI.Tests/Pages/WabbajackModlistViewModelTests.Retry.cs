using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.UI.Tests.Pages;

// "Try again" on a row whose download failed, and for every failed row: which rows offer it, when the buttons are on,
// and that the page hands the work to the acquirer's one retry path.
public partial class WabbajackModlistViewModelTests
{
    private static ArchivePlanRow Failed(string name, string reason = "connection reset")
        => From("host.example", ArchiveState.Manual, name, reason, "https://host.example/a") with { DownloadFailed = true };

    private static async Task<bool> CanRun<T>(ReactiveUI.ReactiveCommand<T, System.Reactive.Unit> command) => await command.CanExecute.FirstAsync();

    [Fact]
    public async Task OnlyARowThatFailedAnAutomaticDownload_OffersTryAgain()
    {
        var (vm, _, _) = await Open(Plan(
            Failed("failed.7z"),
            From("host.example", ArchiveState.Automatic, "stale.7z", "connection reset"),
            From("host.example", ArchiveState.Manual, "by-hand.7z", "get it", "https://host.example/m"),
            From("host.example", ArchiveState.Browser, "browser.7z"),
            From("host.example", ArchiveState.SignIn, "signin.7z", "Sign in to LoversLab to download this"),
            From("host.example", ArchiveState.Automatic, "waiting.7z"),
            From("host.example", ArchiveState.Have, "held.7z")));

        vm.Archives.Where(row => row.CanRetry).Select(row => row.Name).Should().Equal("failed.7z", "stale.7z");
    }

    [Fact]
    public async Task ARowThatFailsOrIsRetried_ChangesItsOfferInPlace()
    {
        var plan = Plan(From("host.example", ArchiveState.Automatic, "a.7z"));
        var (vm, _, acquirer) = await Open(plan);
        using var activation = vm.Activator.Activate();
        var row = vm.Archives.Single();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        acquirer.Change(Plan(Failed("a.7z")));
        await WaitUntil(() => row.CanRetry);

        changed.Should().Contain(nameof(WabbajackArchiveRow.CanRetry));
        vm.HasFailedDownloads.Should().BeTrue();
    }

    [Fact]
    public async Task TryAgainOnARow_AsksTheAcquirerForThatArchiveOnly()
    {
        var (vm, _, acquirer) = await Open(Plan(Failed("one.7z"), Failed("two.7z")));
        var two = vm.Archives.Single(row => row.Name == "two.7z");

        await vm.RetryArchiveCommand.Execute(two);

        acquirer.Retries.Should().ContainSingle().Which.Should().Equal(two.Plan.Archive.XxHash64);
        acquirer.Runs.Should().Be(0, "the list is not planned or run again");
    }

    [Fact]
    public async Task TryAgainOnARowThatCannotBeRetried_AsksForNothing()
    {
        var (vm, _, acquirer) = await Open(Plan(From("host.example", ArchiveState.Manual, "by-hand.7z", "get it", "https://host.example/m")));

        await vm.RetryArchiveCommand.Execute(vm.Archives.Single());

        acquirer.Retries.Should().BeEmpty();
    }

    [Fact]
    public async Task TryAgainForAllFailed_AsksTheAcquirerForEveryFailedRow()
    {
        var (vm, _, acquirer) = await Open(Plan(Failed("one.7z"), Failed("two.7z"), From("host.example", ArchiveState.Manual, "by-hand.7z", "get it")));

        await vm.RetryFailedCommand.Execute();

        acquirer.Retries.Should().ContainSingle().Which.Should().BeNull("null means every failed row");
    }

    [Fact]
    public async Task TheCommands_AreOnlyEnabledWhileSomethingCanBeRetried()
    {
        var (vm, _, acquirer) = await Open(Plan(From("host.example", ArchiveState.Automatic, "a.7z"), From("host.example", ArchiveState.Manual, "by-hand.7z", "get it")));
        using var activation = vm.Activator.Activate();

        vm.HasFailedDownloads.Should().BeFalse();
        (await CanRun(vm.RetryFailedCommand)).Should().BeFalse("no row failed");

        acquirer.Change(Plan(Failed("a.7z"), From("host.example", ArchiveState.Manual, "by-hand.7z", "get it")));
        await WaitUntil(() => vm.HasFailedDownloads);

        (await CanRun(vm.RetryFailedCommand)).Should().BeTrue();
        (await CanRun(vm.RetryArchiveCommand)).Should().BeTrue();
    }

    [Fact]
    public async Task TheCommands_AreOffWhileDownloadsRun_AndComeBackWhenTheyEnd()
    {
        var (vm, _, acquirer) = await Open(Plan(Failed("one.7z"), Failed("two.7z")));
        acquirer.RunGate = new TaskCompletionSource();

        vm.RetryFailedCommand.Execute().Subscribe();
        await WaitUntil(() => vm.IsDownloading);

        vm.CanRetryDownloads.Should().BeFalse();
        (await CanRun(vm.RetryFailedCommand)).Should().BeFalse();
        (await CanRun(vm.RetryArchiveCommand)).Should().BeFalse();

        acquirer.RunGate.SetResult();
        await WaitUntil(() => !vm.IsDownloading);

        (await CanRun(vm.RetryFailedCommand)).Should().BeTrue();
    }

    [Fact]
    public async Task ARetry_ShowsAsDownloadsUnderWay_AndEndsLikeADownload()
    {
        var (vm, _, acquirer) = await Open(Plan(Failed("one.7z"), Row(ArchiveState.Automatic, "waiting.7z")));
        acquirer.RunGate = new TaskCompletionSource();

        vm.RetryFailedCommand.Execute().Subscribe();
        await WaitUntil(() => vm.IsDownloading);

        vm.HasStartedDownloads.Should().BeTrue();
        vm.CanDownload.Should().BeFalse("Download waits for the retry");

        acquirer.RunGate.SetResult();
        await WaitUntil(() => !vm.IsDownloading);
        vm.CanDownload.Should().BeTrue();
    }

    [Fact]
    public async Task APickMadeDuringARetry_StopsItLikeADownload()
    {
        var (vm, _, acquirer) = await Open(Plan(Failed("one.7z")), createsLoadouts: true);
        acquirer.RunGate = new TaskCompletionSource();
        vm.RetryFailedCommand.Execute().Subscribe();
        await WaitUntil(() => vm.IsDownloading);
        acquirer.LastRetryToken.IsCancellationRequested.Should().BeFalse();

        await vm.SelectLoadoutCommand.Execute(CreateNew(vm));

        acquirer.LastRetryToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ARetryThatThrows_ShowsTheErrorAndLeavesDownloadsIdle()
    {
        var (vm, _, acquirer) = await Open(Plan(Failed("one.7z")));
        acquirer.RetryFailure = new IOException("disk full");

        await vm.RetryFailedCommand.Execute();
        await WaitUntil(() => !vm.IsDownloading);

        vm.ErrorText.Should().Be("disk full");
    }

    private static readonly LibraryFileId OtherFile = LibraryFileId.From(EntityId.From(0xBEEF));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryAgain_AfterTheAcquirerShowedAnotherList_PutsThisListsRowsBackFirst(bool perRow)
    {
        var mine = new ArchivePlan([Failed("mine.7z")], SomeFile);
        var (vm, _, acquirer) = await Open(mine);
        // Another page took the singleton over while this one was away.
        acquirer.Show(new ArchivePlan([Failed("theirs.7z")], OtherFile));

        if (perRow) await vm.RetryArchiveCommand.Execute(vm.Archives.Single(row => row.Name == "mine.7z"));
        else await vm.RetryFailedCommand.Execute();

        acquirer.Shown.Last().Modlist.Should().Be(SomeFile, "the retry acts on this page's rows, not the other list's");
        acquirer.Shown.Last().Rows.Select(row => row.Archive.Name.ToString()).Should().Equal("mine.7z");
        acquirer.RetriedLists.Should().ContainSingle().Which.Should().Be(SomeFile);
    }

    [Fact]
    public async Task TryAgain_WhileTheAcquirerStillHoldsThisList_DoesNotShowItAgain()
    {
        var (vm, _, acquirer) = await Open(new ArchivePlan([Failed("mine.7z")], SomeFile));
        var shown = acquirer.Shown.Count;

        await vm.RetryFailedCommand.Execute();

        acquirer.Shown.Should().HaveCount(shown);
    }
}
