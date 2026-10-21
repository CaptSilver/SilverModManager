using FluentAssertions;
using NexusMods.App.UI.Pages.CollectionDownload;
using NexusMods.Collections;

namespace NexusMods.UI.Tests.Pages;

public class CollectionInstallProgressTextTests
{
    private static CollectionInstallProgress.Snapshot Snapshot(
        int total = 46,
        int alreadyInstalled = 241,
        int installed = 0,
        string[]? failed = null,
        string[]? installing = null,
        bool isFinished = false,
        bool leftDisabled = false) => new(
        total, alreadyInstalled, installed,
        (failed ?? []).Select(f => new CollectionInstallProgress.FailedMod(f, $"{f} reason")).ToArray(),
        installing ?? [],
        isFinished, leftDisabled);

    [Fact]
    public void Summary_CountsModsInstalledBeforeThisRunTowardsTheWholeCollection()
    {
        CollectionInstallProgressText.Summary(Snapshot(installed: 4)).Should().Be("Installing 245 of 287");
    }

    [Fact]
    public void Summary_SaysInstalledOnceTheJobIsFinished()
    {
        CollectionInstallProgressText.Summary(Snapshot(installed: 45, failed: ["Broken"], isFinished: true)).Should().Be("Installed 286 of 287");
    }

    [Fact]
    public void Installing_ListsTheNamesAndFoldsTheRestIntoACount()
    {
        var names = Enumerable.Range(0, 8).Select(i => $"Mod {i}").ToArray();

        CollectionInstallProgressText.Installing(Snapshot(installing: names[..2])).Should().Be("Now installing: Mod 0, Mod 1");
        CollectionInstallProgressText.Installing(Snapshot(installing: names)).Should().Be("Now installing: Mod 0, Mod 1, Mod 2, Mod 3, Mod 4 and 3 more");
    }

    [Fact]
    public void Installing_IsEmptyWhenNothingIsRunning()
    {
        CollectionInstallProgressText.Installing(Snapshot()).Should().BeEmpty();
    }

    [Fact]
    public void FailedLines_PairEachModWithItsReason()
    {
        CollectionInstallProgressText.FailedLines(Snapshot(failed: ["A", "B"])).Should().Equal("A: A reason", "B: B reason");
    }

    [Fact]
    public void DisabledNotice_AppearsOnlyWhenTheFinishedInstallLeftTheCollectionDisabled()
    {
        CollectionInstallProgressText.DisabledNotice(Snapshot(failed: ["Broken"], isFinished: true, leftDisabled: true))
            .Should().Be("The collection was left disabled because required mods failed to install.");
        CollectionInstallProgressText.DisabledNotice(Snapshot(failed: ["Broken"], isFinished: true, leftDisabled: false)).Should().BeEmpty();
        CollectionInstallProgressText.DisabledNotice(Snapshot(failed: ["Broken"], isFinished: false, leftDisabled: true)).Should().BeEmpty();
    }

    [Fact]
    public void DisabledNotice_IsEmptyWhenNothingFailedInThisRun()
    {
        // An optional-only run can finish cleanly while required mods are still missing; blaming failures would be wrong.
        CollectionInstallProgressText.DisabledNotice(Snapshot(isFinished: true, leftDisabled: true)).Should().BeEmpty();
    }
}
