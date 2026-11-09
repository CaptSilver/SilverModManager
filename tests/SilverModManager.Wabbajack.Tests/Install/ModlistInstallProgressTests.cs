using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Wabbajack.Install;

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
}
