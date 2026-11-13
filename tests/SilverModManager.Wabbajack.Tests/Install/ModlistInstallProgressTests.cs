using System.Reactive.Linq;
using FluentAssertions;
using NexusMods.Paths;
using DynamicData;
using SilverModManager.Sdk.Loadouts.Install;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

public class ModlistInstallProgressTests
{
    [Fact]
    public void ANewProgressIsInTheCheckPhaseWithNothingDone()
    {
        var progress = new ModlistInstallProgress();

        progress.Phase.Should().Be(InstallPhase.Check);
        progress.DoneItems.Should().Be(0);
        progress.TotalItems.Should().Be(0);
        progress.DroppedCount.Should().Be(0);
    }

    [Fact]
    public void EachUpdateIsPublishedToObserversInOrder()
    {
        var progress = new ModlistInstallProgress();
        var seen = new List<(InstallPhase Phase, int Done)>();
        using var subscription = progress.Changes.Subscribe(s => seen.Add((s.Phase, s.DoneItems)));

        progress.Enter(InstallPhase.Resolve, totalItems: 10, totalBytes: 100);
        progress.Advance(items: 4, bytes: 40);
        progress.Advance(items: 6, bytes: 60);
        progress.AddDropped(3);
        progress.SetEstimate(Size.From(512), isLowerBound: true);

        seen.Should().Equal((InstallPhase.Check, 0), (InstallPhase.Resolve, 0), (InstallPhase.Resolve, 4), (InstallPhase.Resolve, 10), (InstallPhase.Resolve, 10), (InstallPhase.Resolve, 10));
        progress.DoneBytes.Should().Be(100);
        progress.TotalBytes.Should().Be(100);
        progress.DroppedCount.Should().Be(3);
        progress.EstimatedExtractedSize.Value.Should().Be(512);
        progress.IsEstimateLowerBound.Should().BeTrue();
    }

    [Fact]
    public async Task UpdatesFromSeveralThreadsAreAllCounted()
    {
        var progress = new ModlistInstallProgress();
        progress.Enter(InstallPhase.Write, totalItems: 4000, totalBytes: 4000);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) progress.Advance(1, 1);
        })));

        progress.DoneItems.Should().Be(4000);
        progress.DoneBytes.Should().Be(4000);
    }

    [Fact]
    public void AnArchiveBeingBuiltIsReportedWithItsOwnFileCountUntilItEnds()
    {
        var progress = new ModlistInstallProgress();
        progress.Enter(InstallPhase.Write, totalItems: 10, totalBytes: 100);
        var seen = new List<ModlistInstallProgress.ArchiveBuild?>();
        using var subscription = progress.Changes.Subscribe(s => seen.Add(s.Archive));

        progress.BeginArchive("Textures.bsa", number: 3, count: 12, totalFiles: 58);
        progress.AdvanceArchive(3);
        progress.AdvanceArchive(3);
        progress.EndArchive(3);

        seen.Should().Equal(
            null,
            new ModlistInstallProgress.ArchiveBuild("Textures.bsa", 3, 12, 0, 58),
            new ModlistInstallProgress.ArchiveBuild("Textures.bsa", 3, 12, 1, 58),
            new ModlistInstallProgress.ArchiveBuild("Textures.bsa", 3, 12, 2, 58),
            null);
        progress.DoneItems.Should().Be(0, "an archive's files are not items of the phase");
    }

    [Fact]
    public void ArchivesBuildingAtTheSameTimeEachKeepTheirOwnCount()
    {
        var progress = new ModlistInstallProgress();
        progress.BeginArchive("A.bsa", 1, 2, 5);
        progress.BeginArchive("B.bsa", 2, 2, 5);

        progress.AdvanceArchive(1);
        progress.AdvanceArchive(1);
        progress.Current.Archive.Should().Be(new ModlistInstallProgress.ArchiveBuild("A.bsa", 1, 2, 2, 5), "A reported last, so it is the one shown");

        progress.AdvanceArchive(2);
        progress.Current.Archive.Should().Be(new ModlistInstallProgress.ArchiveBuild("B.bsa", 2, 2, 1, 5));
        progress.AdvanceArchive(1);
        progress.Current.Archive!.DoneFiles.Should().Be(3, "B taking the report did not lose A's progress");
    }

    [Fact]
    public void WhenTheShownArchiveEndsTheOneStillBuildingTakesItsPlace()
    {
        var progress = new ModlistInstallProgress();
        progress.BeginArchive("A.bsa", 1, 2, 5);
        progress.AdvanceArchive(1);
        progress.BeginArchive("B.bsa", 2, 2, 5);

        progress.EndArchive(2);

        progress.Current.Archive.Should().Be(new ModlistInstallProgress.ArchiveBuild("A.bsa", 1, 2, 1, 5));

        progress.EndArchive(1);
        progress.Current.Archive.Should().BeNull();
    }

    [Fact]
    public void TheBuildsSummaryShowsTheOldestRunningArchiveEvenWhenANewerOneReportedLast()
    {
        var progress = new ModlistInstallProgress();
        progress.BeginArchive("A.bsa", 1, 3, 5);
        progress.BeginArchive("B.bsa", 2, 3, 5);
        progress.AdvanceArchive(2);

        progress.Current.Archive!.Name.Should().Be("B.bsa");
        progress.Current.Builds!.Shown.Name.Should().Be("A.bsa");
        progress.Current.Builds.Running.Should().Be(2);
        progress.Current.Builds.Count.Should().Be(3);
    }

    [Fact]
    public void TheBuildsSummaryCountsArchivesThatFinishedAndIsNullWhenNoneIsBuilding()
    {
        var progress = new ModlistInstallProgress();
        progress.Current.Builds.Should().BeNull();

        progress.BeginArchive("A.bsa", 1, 3, 5);
        progress.BeginArchive("B.bsa", 2, 3, 5);
        progress.EndArchive(1);

        progress.Current.Builds!.Done.Should().Be(1);
        progress.Current.Builds.Running.Should().Be(1);
        progress.Current.Builds.Shown.Name.Should().Be("B.bsa");

        progress.EndArchive(2);
        progress.Current.Builds.Should().BeNull();
        progress.BeginArchive("C.bsa", 3, 3, 5);
        progress.Current.Builds!.Done.Should().Be(2);
    }

    [Fact]
    public void TwoArchivesWithTheSameFileNameAreCountedSeparately()
    {
        var progress = new ModlistInstallProgress();
        progress.BeginArchive("Textures.bsa", 1, 2, 4);
        progress.BeginArchive("Textures.bsa", 2, 2, 4);

        progress.AdvanceArchive(1);
        progress.EndArchive(2);

        progress.Current.Archive.Should().Be(new ModlistInstallProgress.ArchiveBuild("Textures.bsa", 1, 2, 1, 4));
    }

    [Fact]
    public void FilesPackedIntoRunningArchivesCountAsPartOfTheItemsTheyAre()
    {
        var progress = new ModlistInstallProgress();
        progress.Enter(InstallPhase.Write, totalItems: 10, totalBytes: 100);
        progress.BeginArchive("A.bsa", 1, 2, 4);
        progress.BeginArchive("B.bsa", 2, 2, 10);

        progress.AdvanceArchive(1);
        progress.AdvanceArchive(1);
        progress.AdvanceArchive(2);
        progress.Current.ArchiveItems.Should().BeApproximately(0.5 + 0.1, 1e-9);

        progress.EndArchive(1);
        progress.Current.ArchiveItems.Should().BeApproximately(0.1, 1e-9, "an ended archive is an item of the phase, which Advance counts");
    }

    [Fact]
    public void AnArchiveWithEveryFilePackedStillHasItsLastStepToDo()
    {
        var progress = new ModlistInstallProgress();
        progress.BeginArchive("A.bsa", 1, 1, 2);
        progress.AdvanceArchive(1);
        progress.AdvanceArchive(1);

        progress.Current.ArchiveItems.Should().BeLessThan(1, "compressing and writing the archive follows the last file");
    }

    private static ModInstallRow Row(ModlistInstallProgress progress, string folder)
    {
        progress.Mods.Should().BeAssignableTo<ModInstallRows>();
        ((ModInstallRows)progress.Mods).TryGet(folder, out var row).Should().BeTrue();
        return row;
    }

    private static ModInstallTotals LastTotals(ModlistInstallProgress progress)
    {
        ModInstallTotals? last = null;
        using var subscription = progress.Mods.Totals.Subscribe(t => last = t);
        return last!;
    }

    [Fact]
    public void AdvanceInResolveMovesTheFolderToExtracting()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 0, 0)]);
        progress.Enter(InstallPhase.Resolve, 2, 20);

        progress.Advance("A", 1, 10);

        var state = Row(progress, "A").Current;
        state.State.Should().Be(ModInstallState.Extracting);
        state.Resolved.Should().Be(1);
        state.Done.Should().Be(1);
    }

    [Fact]
    public void AdvanceInWriteMovesTheFolderToInstallingAndTheLastOneToInstalled()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 0, 0)]);
        progress.Enter(InstallPhase.Write, 2, 20);

        progress.Advance("A", 1, 10);
        Row(progress, "A").Current.Should().Match<ModInstallRowState>(s => s.State == ModInstallState.Installing && s.Written == 1);

        progress.Advance("A", 1, 10);
        Row(progress, "A").Current.State.Should().Be(ModInstallState.Installed);
        LastTotals(progress).Should().Be(new ModInstallTotals(1, 1, 0));
    }

    [Fact]
    public void AFolderAlreadyDoneByAnEarlierRunStartsInstalled()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 3, 3, 0, 0)]);

        Row(progress, "A").Current.Should().Match<ModInstallRowState>(s => s.State == ModInstallState.Installed && s.Written == 3);
        LastTotals(progress).Should().Be(new ModInstallTotals(1, 1, 0));
    }

    [Fact]
    public void AFolderPartlyDoneStartsInstallingAtThatCount()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 500, 498, 0, 0)]);

        Row(progress, "A").Current.Should().Match<ModInstallRowState>(s => s.State == ModInstallState.Installing && s.Written == 498 && s.Resolved == 498);
    }

    [Fact]
    public void LooseFilesDoneWithABsaAheadWaitsForTheArchivePass()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 3, 0, 1, 0)]);
        progress.Enter(InstallPhase.Write, 3, 30);

        progress.Advance("A", 1, 10);
        progress.Advance("A", 1, 10);
        Row(progress, "A").Current.Should().Match<ModInstallRowState>(s => s.State == ModInstallState.Installing && s.Detail == " · waiting for the archive pass");

        progress.Advance("A", 1, 10);
        Row(progress, "A").Current.Should().Match<ModInstallRowState>(s => s.State == ModInstallState.Installed && s.Detail == null);
    }

    [Fact]
    public void LooseFilesDoneWithOnlyAMergeAheadWaitsForTheMergePass()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 1, 1)]);
        progress.Enter(InstallPhase.Write, 2, 20);

        progress.Advance("A", 1, 10);

        Row(progress, "A").Current.Detail.Should().Be(" · waiting for the merge pass");
    }

    [Fact]
    public void MarkFailedTurnsTheRowFailedWithTheReasonAndLaterAdvancesKeepIt()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 0, 0)]);
        progress.Enter(InstallPhase.Write, 2, 20);

        progress.MarkFailed("A", SkipKind.MissingArchive, "the archive is missing");
        progress.Advance("A", 1, 10);
        progress.Advance("A", 1, 10);

        Row(progress, "A").Current.Should().Match<ModInstallRowState>(s => s.State == ModInstallState.Failed && s.Reason == "the archive is missing");
        LastTotals(progress).Should().Be(new ModInstallTotals(1, 0, 1));
    }

    [Fact]
    public void ABySkipKindIsNotAFailure()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 0, 0)]);

        progress.MarkFailed("A", SkipKind.KeptOriginal, "kept the user's file");

        Row(progress, "A").Current.State.Should().Be(ModInstallState.Waiting);
        LastTotals(progress).Failed.Should().Be(0);
    }

    [Fact]
    public void ASecondFailureKeepsTheFirstReason()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 0, 0)]);

        progress.MarkFailed("A", SkipKind.MissingArchive, "first");
        progress.MarkFailed("A", SkipKind.MemberNotFound, "second");

        Row(progress, "A").Current.Reason.Should().Be("first");
        LastTotals(progress).Failed.Should().Be(1);
    }

    [Fact]
    public void MarkFailedForAnUnknownFolderChangesNothing()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 0, 0)]);

        progress.MarkFailed("Z", SkipKind.MissingArchive, "nobody's");

        LastTotals(progress).Should().Be(new ModInstallTotals(1, 0, 0));
        Row(progress, "A").Current.State.Should().Be(ModInstallState.Waiting);
    }

    [Fact]
    public void MarkArchiveBuildPutsTheBuildOnTheRowUntilItEnds()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 1, 0)]);
        progress.Enter(InstallPhase.Write, 2, 20);

        progress.BeginArchive("X.bsa", 1, 1, 10);
        for (var i = 0; i < 3; i++) progress.AdvanceArchive(1);
        progress.MarkArchiveBuild("A", 1);
        Row(progress, "A").Current.Detail.Should().Be(" · building X.bsa (3 of 10)");

        progress.EndArchive(1);
        progress.MarkArchiveBuild("A", 1);
        Row(progress, "A").Current.Detail.Should().BeNull();
    }

    [Fact]
    public void AFolderBuildingTwoArchivesKeepsShowingTheOtherWhenOneEnds()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 3, 0, 2, 0)]);
        progress.Enter(InstallPhase.Write, 3, 30);

        progress.BeginArchive("A.bsa", 1, 2, 4);
        progress.MarkArchiveBuild("A", 1);
        progress.BeginArchive("A - Textures.bsa", 2, 2, 6);
        progress.MarkArchiveBuild("A", 2);
        progress.AdvanceArchive(1);
        progress.MarkArchiveBuild("A", 1);
        Row(progress, "A").Current.Detail.Should().Be(" · building A.bsa (1 of 4)");

        progress.EndArchive(1);
        progress.MarkArchiveBuild("A", 1);
        Row(progress, "A").Current.Detail.Should().Be(" · building A - Textures.bsa (0 of 6)", "the folder's other archive is still being built");

        progress.EndArchive(2);
        progress.MarkArchiveBuild("A", 2);
        Row(progress, "A").Current.Detail.Should().BeNull();
    }

    [Fact]
    public void ConnectBeforeSetModsGetsTheAddBurstOnce()
    {
        var progress = new ModlistInstallProgress();
        var before = progress.Mods;
        var sets = new List<IChangeSet<ModInstallRow, string>>();
        using var subscription = progress.Mods.Connect().Subscribe(sets.Add);
        sets.Should().BeEmpty("an empty cache sends no blank frame");

        progress.SetMods([new ModFolderPlan("B", 1, 1, 0, 0, 0), new ModFolderPlan("A", 0, 1, 0, 0, 0), new ModFolderPlan("C", 2, 1, 0, 0, 0)]);

        progress.Mods.Should().BeSameAs(before);
        sets.Should().ContainSingle("the rows arrive as one burst, not one change set per row");
        var adds = sets[0].Where(c => c.Reason == ChangeReason.Add).Select(c => c.Key).ToList();
        adds.Should().BeEquivalentTo(["A", "B", "C"]);
        adds.Should().OnlyHaveUniqueItems();
        progress.Mods.Count.Should().Be(3);
    }

    [Fact]
    public void RowsAreAddedInTheOrderOfTheirIndex()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("B", 1, 1, 0, 0, 0), new ModFolderPlan("A", 0, 1, 0, 0, 0)]);

        var keys = new List<string>();
        using var subscription = progress.Mods.Connect().Subscribe(set => keys.AddRange(set.Select(c => c.Key)));

        keys.Should().Equal("A", "B");
    }

    [Fact]
    public void AdvanceWithNoFolderOnlyMovesTheSnapshot()
    {
        var progress = new ModlistInstallProgress();
        progress.SetMods([new ModFolderPlan("A", 0, 2, 0, 0, 0)]);
        progress.Enter(InstallPhase.Write, 2, 20);

        progress.Advance(1, 10);

        progress.DoneItems.Should().Be(1);
        Row(progress, "A").Current.State.Should().Be(ModInstallState.Waiting);
    }

    [Fact]
    public void FinishingThreeThousandFoldersRaisesOneSnapshotAndOneTotalsPerFolder()
    {
        const int folders = 3000;
        var progress = new ModlistInstallProgress();
        progress.SetMods(Enumerable.Range(0, folders).Select(i => new ModFolderPlan($"F{i}", i, 2, 0, 0, 0)).ToArray());
        progress.Enter(InstallPhase.Resolve, folders * 2, 0);
        for (var i = 0; i < folders; i++) progress.Advance($"F{i}", 1, 0);
        progress.Enter(InstallPhase.Write, folders * 2, 0);
        for (var i = 0; i < folders; i++) progress.Advance($"F{i}", 1, 0);

        var changes = 0;
        var totals = 0;
        using var changesSubscription = progress.Changes.Skip(1).Subscribe(_ => changes++);
        using var totalsSubscription = progress.Mods.Totals.Skip(1).Subscribe(_ => totals++);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < folders; i++) progress.Advance($"F{i}", 1, 0);

        changes.Should().Be(folders);
        totals.Should().Be(folders);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }
}
