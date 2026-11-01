using FluentAssertions;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

public class InstallEstimatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static ModlistInstallProgress.Snapshot Items(int done, int total, InstallPhase phase = InstallPhase.Write)
        => ModlistInstallProgress.Snapshot.Empty with { Phase = phase, DoneItems = done, TotalItems = total };

    private static ModlistInstallProgress.Snapshot Bytes(int doneItems, int totalItems, long doneBytes, long totalBytes)
        => ModlistInstallProgress.Snapshot.Empty with
        {
            Phase = InstallPhase.Write, DoneItems = doneItems, TotalItems = totalItems, DoneBytes = doneBytes, TotalBytes = totalBytes,
        };

    [Fact]
    public void NothingIsSaidBeforeAMinuteOfDataThenTheRemainingTimeFollowsTheRate()
    {
        var estimator = new InstallEstimator();

        estimator.Add(Start, Items(0, 600)).Should().BeNull();
        estimator.Add(Start.AddSeconds(30), Items(30, 600)).Should().BeNull();
        estimator.Add(Start.AddSeconds(59), Items(59, 600)).Should().BeNull();
        estimator.Add(Start.AddSeconds(60), Items(60, 600)).Should().Be(TimeSpan.FromSeconds(540));
    }

    [Fact]
    public void ASpeedUpShortensTheEstimateAsTheSlowStartLeavesTheWindow()
    {
        var estimator = new InstallEstimator();
        estimator.Add(Start, Items(0, 10_000));
        // One item a second for the first five minutes, then ten a second.
        for (var second = 1; second <= 300; second++) estimator.Add(Start.AddSeconds(second), Items(second, 10_000));
        var slow = estimator.Add(Start.AddSeconds(300), Items(300, 10_000));
        for (var second = 301; second <= 600; second++) estimator.Add(Start.AddSeconds(second), Items(300 + (second - 300) * 10, 10_000));

        var fast = estimator.Add(Start.AddSeconds(600), Items(3300, 10_000));

        slow.Should().BeCloseTo(TimeSpan.FromSeconds(9700), TimeSpan.FromSeconds(1));
        fast.Should().BeCloseTo(TimeSpan.FromSeconds(670), TimeSpan.FromSeconds(10), "only the last five minutes count, and they ran at ten items a second");
    }

    [Fact]
    public void BytesDecideWhenThePhaseCountsThemSinceOneBigFileIsNotOneSmallOne()
    {
        var estimator = new InstallEstimator();

        estimator.Add(Start, Bytes(0, 100, 0, 1_000_000));
        // Half the items are done in a minute but only a tenth of the bytes.
        var left = estimator.Add(Start.AddSeconds(60), Bytes(50, 100, 100_000, 1_000_000));

        left.Should().BeCloseTo(TimeSpan.FromSeconds(540), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ANewPhaseStartsCollectingAgain()
    {
        var estimator = new InstallEstimator();
        estimator.Add(Start, Items(0, 100, InstallPhase.Resolve));
        estimator.Add(Start.AddSeconds(90), Items(90, 100, InstallPhase.Resolve)).Should().NotBeNull();

        estimator.Add(Start.AddSeconds(91), Items(0, 100, InstallPhase.Write)).Should().BeNull();
        estimator.Add(Start.AddSeconds(120), Items(10, 100, InstallPhase.Write)).Should().BeNull();
        estimator.Add(Start.AddSeconds(151), Items(30, 100, InstallPhase.Write)).Should().NotBeNull();
    }

    [Fact]
    public void NoProgressInTheWindowGivesNoEstimate()
    {
        var estimator = new InstallEstimator();
        estimator.Add(Start, Items(5, 100));

        estimator.Add(Start.AddMinutes(2), Items(5, 100)).Should().BeNull();
    }

    [Fact]
    public void APhaseWithoutItemsHasNothingToEstimate()
    {
        var estimator = new InstallEstimator();
        estimator.Add(Start, Items(0, 0, InstallPhase.Order));

        estimator.Add(Start.AddMinutes(3), Items(0, 0, InstallPhase.Order)).Should().BeNull();
    }
}
