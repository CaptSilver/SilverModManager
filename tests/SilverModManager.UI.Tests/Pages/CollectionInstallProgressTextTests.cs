using FluentAssertions;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.Collections;
using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.UI.Tests.Pages;

public class CollectionInstallProgressTextTests(IServiceProvider provider) : AUiTest(provider)
{
    private static CollectionInstallProgress.Snapshot Snapshot(
        int total = 46,
        int alreadyInstalled = 241,
        int installed = 0,
        int failed = 0,
        bool isFinished = false,
        bool leftDisabled = false) => new(total, alreadyInstalled, installed, failed, isFinished, leftDisabled);

    [Fact]
    public void Summary_CountsModsInstalledBeforeThisRunTowardsTheWholeCollection()
    {
        CollectionInstallProgressText.Summary(Snapshot(installed: 4)).Should().Be("Installing 245 of 287");
    }

    [Fact]
    public void Summary_SaysInstalledOnceTheJobIsFinished()
    {
        CollectionInstallProgressText.Summary(Snapshot(installed: 45, failed: 1, isFinished: true)).Should().Be("Installed 286 of 287");
    }

    [Fact]
    public void DisabledNotice_AppearsOnlyWhenTheFinishedInstallLeftTheCollectionDisabled()
    {
        CollectionInstallProgressText.DisabledNotice(Snapshot(failed: 1, isFinished: true, leftDisabled: true))
            .Should().Be("The collection was left disabled because required mods failed to install.");
        CollectionInstallProgressText.DisabledNotice(Snapshot(failed: 1, isFinished: true, leftDisabled: false)).Should().BeEmpty();
        CollectionInstallProgressText.DisabledNotice(Snapshot(failed: 1, isFinished: false, leftDisabled: true)).Should().BeEmpty();
    }

    [Fact]
    public void DisabledNotice_IsEmptyWhenNothingFailedInThisRun()
    {
        // An optional-only run can finish cleanly while required mods are still missing; blaming failures would be wrong.
        CollectionInstallProgressText.DisabledNotice(Snapshot(isFinished: true, leftDisabled: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task RowSummary_AndRowDetail_TakeAReasonWithBraces()
    {
        const string reason = "{mods}/x and {0}";
        var failed = new ModInstallRowState(ModInstallState.Failed, 0, 0, 1, Reason: reason);
        var (row, download) = await PendingReviewRowTests.CreateRow(Connection, ReviewKind.NoInstaller, reason);

        var summary = () => ModInstallText.Summary(failed);
        var review = new PendingReviewRow(row, download);

        summary.Should().NotThrow().Which.Should().Be(reason);
        review.Detail.Should().Be(reason);
    }
}
