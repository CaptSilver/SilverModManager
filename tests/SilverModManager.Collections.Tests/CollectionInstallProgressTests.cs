using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.Collections.Tests;

public class CollectionInstallProgressTests
{
    private static CollectionInstallProgress Begun(params (int Index, string Name, bool AlreadyInstalled)[] mods)
    {
        var progress = new CollectionInstallProgress();
        progress.Begin(mods.Count(static m => !m.AlreadyInstalled), mods.Count(static m => m.AlreadyInstalled), mods);
        return progress;
    }

    private static ModInstallRow Row(CollectionInstallProgress progress, int index)
    {
        progress.Mods.Should().BeOfType<ModInstallRows>().Which.TryGet(index.ToString(), out var row).Should().BeTrue();
        return row!;
    }

    [Fact]
    public void BeginAddsOneRowPerModWithAlreadyInstalledOnesInstalled()
    {
        var progress = Begun((0, "Old", true), (1, "New", false));

        progress.Mods.Count.Should().Be(2);
        Row(progress, 0).Current.Should().Be(new ModInstallRowState(ModInstallState.Installed, 1, 1, 1));
        Row(progress, 1).Current.Should().Be(new ModInstallRowState(ModInstallState.Waiting, 0, 0, 1));
        Row(progress, 1).Name.Should().Be("New");
    }

    [Fact]
    public void ModsWithTheSameNameGetOneRowEach()
    {
        var progress = Begun((0, "Twin", false), (1, "Twin", false));

        progress.Mods.Count.Should().Be(2);
    }

    [Fact]
    public void ModStartedAndModInstalledMoveTheRowAndTheTotals()
    {
        var progress = Begun((0, "A", false), (1, "B", true));

        progress.ModStarted(0);
        Row(progress, 0).Current.State.Should().Be(ModInstallState.Installing);

        progress.ModInstalled(0);
        Row(progress, 0).Current.Should().Be(new ModInstallRowState(ModInstallState.Installed, 1, 1, 1));
        TotalsOf(progress).Should().Be(new ModInstallTotals(2, 2, 0));
    }

    [Fact]
    public void ModFailedTurnsTheRowFailedWithTheReason()
    {
        var progress = Begun((0, "A", false));

        progress.ModStarted(0);
        progress.ModFailed(0, "broken on purpose");

        Row(progress, 0).Current.State.Should().Be(ModInstallState.Failed);
        Row(progress, 0).Current.Reason.Should().Be("broken on purpose");
        TotalsOf(progress).Should().Be(new ModInstallTotals(1, 0, 1));
    }

    [Fact]
    public void ModNeedsReviewTurnsTheRowNeedsUserWithTheReviewReason()
    {
        var progress = Begun((0, "A", false));

        progress.ModStarted(0);
        progress.ModNeedsReview(0, ReviewKind.NoInstaller, "no matching files");

        Row(progress, 0).Current.State.Should().Be(ModInstallState.NeedsUser);
        Row(progress, 0).Current.Reason.Should().Be("no matching files");
        TotalsOf(progress).Should().Be(new ModInstallTotals(1, 0, 0));
    }

    [Fact]
    public void FinishPutsARowThatWasStillRunningBackToWaiting()
    {
        var progress = Begun((0, "A", false));

        progress.ModStarted(0);
        progress.Finish(leftDisabled: false);

        Row(progress, 0).Current.State.Should().Be(ModInstallState.Waiting);
    }

    [Fact]
    public void TheSnapshotCarriesCountsOnly()
    {
        var progress = Begun((0, "Old", true), (1, "A", false), (2, "B", false));
        progress.ModStarted(1);
        progress.ModInstalled(1);
        progress.ModStarted(2);
        progress.ModFailed(2, "no");
        progress.Finish(leftDisabled: true);

        progress.Current.Should().Be(new CollectionInstallProgress.Snapshot(Total: 2, AlreadyInstalled: 1, Installed: 1, Failed: 1, IsFinished: true, LeftDisabled: true));
    }

    private static ModInstallTotals TotalsOf(CollectionInstallProgress progress)
    {
        ModInstallTotals seen = default!;
        using var _ = progress.Mods.Totals.Subscribe(t => seen = t);
        return seen;
    }
}
