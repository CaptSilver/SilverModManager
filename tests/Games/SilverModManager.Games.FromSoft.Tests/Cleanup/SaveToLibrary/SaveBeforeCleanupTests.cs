using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup.SaveToLibrary;

public class SaveBeforeCleanupTests
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(4333);
    private static readonly ModToSave A = new("a", "Mod A", "1.0", 1, 11);
    private static readonly ModToSave B = new("b", "Mod B", "2.0", 2, 22);

    private static SavePlan PlanOf(params ModToSave[] mods) => new(Game, [..mods], []);

    [Fact]
    public async Task EveryChosenModIsDownloadedBeforeTheCleanupMayGoOn()
    {
        var log = new List<string>();
        var prompts = new FakePrompts(log: log);
        var downloader = new FakeDownloader();

        var outcome = await SaveBeforeCleanup.RunAsync(PlanOf(A, B), downloader, prompts, default);

        outcome.Proceed.Should().BeTrue();
        outcome.Report.Added.Should().Equal(A, B);
        log.Should().Equal("choose");
        downloader.Log.Should().Equal("download a", "download b");
    }

    [Fact]
    public async Task CancellingTheListDownloadsNothingAndStopsTheCleanup()
    {
        var downloader = new FakeDownloader();

        var outcome = await SaveBeforeCleanup.RunAsync(PlanOf(A), downloader, new FakePrompts(choose: _ => null), default);

        outcome.Proceed.Should().BeFalse();
        downloader.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyTheModsStillCheckedAreDownloaded()
    {
        var downloader = new FakeDownloader();

        var outcome = await SaveBeforeCleanup.RunAsync(PlanOf(A, B), downloader, new FakePrompts(choose: _ => ImmutableArray.Create(B)), default);

        outcome.Proceed.Should().BeTrue();
        downloader.Log.Should().Equal("download b");
    }

    [Fact]
    public async Task AModTheListNeverOfferedIsNotDownloaded()
    {
        var downloader = new FakeDownloader();
        var stranger = new ModToSave("z", "Stranger", "1", 9, 99);

        await SaveBeforeCleanup.RunAsync(PlanOf(A), downloader, new FakePrompts(choose: _ => ImmutableArray.Create(A, stranger)), default);

        downloader.Log.Should().Equal("download a");
    }

    [Fact]
    public async Task AFailedDownloadIsReportedAndTheUserDecidesWhetherToGoOn()
    {
        var downloader = new FakeDownloader(m => m == A ? ModDownloadResult.Failed("404") : ModDownloadResult.Added);
        SaveReport? shown = null;
        var prompts = new FakePrompts(proceedAfterFailures: r =>
        {
            shown = r;
            return true;
        });

        var outcome = await SaveBeforeCleanup.RunAsync(PlanOf(A, B), downloader, prompts, default);

        outcome.Proceed.Should().BeTrue();
        shown!.Failed.Should().ContainSingle().Which.Should().Be(new FailedDownload(A, "404"));
        outcome.Report.Added.Should().Equal(B);
        prompts.Log.Should().Equal("choose", "failures");
    }

    [Fact]
    public async Task CancellingAfterAFailureStopsTheCleanup()
    {
        var downloader = new FakeDownloader(_ => ModDownloadResult.Failed("boom"));

        var outcome = await SaveBeforeCleanup.RunAsync(PlanOf(A), downloader, new FakePrompts(proceedAfterFailures: _ => false), default);

        outcome.Proceed.Should().BeFalse();
    }

    [Fact]
    public async Task ADownloaderThatThrowsCountsAsAFailureAndTheRestStillRun()
    {
        var downloader = new FakeDownloader(m => m == A ? throw new InvalidOperationException("no premium key") : ModDownloadResult.Added);

        var outcome = await SaveBeforeCleanup.RunAsync(PlanOf(A, B), downloader, new FakePrompts(), default);

        outcome.Report.Failed.Should().ContainSingle().Which.Message.Should().Contain("no premium key");
        outcome.Report.Added.Should().Equal(B);
    }

    [Fact]
    public async Task APageOpenedInTheBrowserDoesNotBlockTheCleanup()
    {
        var downloader = new FakeDownloader(_ => ModDownloadResult.OpenedInBrowser);
        var prompts = new FakePrompts();

        var outcome = await SaveBeforeCleanup.RunAsync(PlanOf(A, B), downloader, prompts, default);

        outcome.Proceed.Should().BeTrue();
        outcome.Report.OpenedInBrowser.Should().Equal(A, B);
        prompts.Log.Should().Equal("choose");
    }

    [Fact]
    public async Task AListWithNothingToSaveIsStillShownSoNothingIsLostSilently()
    {
        var plan = new SavePlan(Game, [], [new ListedMod("me3-host", ListedReason.GitHub, "GitHub releases are not downloaded")]);
        var downloader = new FakeDownloader();
        var prompts = new FakePrompts();

        var outcome = await SaveBeforeCleanup.RunAsync(plan, downloader, prompts, default);

        outcome.Proceed.Should().BeTrue();
        prompts.Log.Should().Equal("choose");
        downloader.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyPlanAsksNothing()
    {
        var prompts = new FakePrompts();

        var outcome = await SaveBeforeCleanup.RunAsync(new SavePlan(Game, [], []), new FakeDownloader(), prompts, default);

        outcome.Proceed.Should().BeTrue();
        prompts.Log.Should().BeEmpty();
    }

    [Fact]
    public void ReportsOfTwoOffersAddUpInOrder()
    {
        var first = new SaveReport([A], [], [new FailedDownload(B, "404")]);
        var second = new SaveReport([B], [A], []);

        var total = first.Plus(second);

        total.Added.Should().Equal(A, B);
        total.OpenedInBrowser.Should().Equal(A);
        total.Failed.Should().Equal(new FailedDownload(B, "404"));
    }
}
