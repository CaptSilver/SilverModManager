using FluentAssertions;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Cli;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class BatchStatusTextTests
{
    [Fact]
    public void AFinishedBatchSaysItIsNotRunningAndShowsNoWaitingLinesWithoutAPlan()
    {
        var lines = BatchStatusText.Describe(new AcquireStatus(null, false, 4, 4, 0, TimeSpan.Zero, []), "List", new ArchivePlan([]));

        lines.Should().Contain("Downloading 'List': not running (the last batch has ended)").And.Contain("Archives: 4 of 4 done");
        lines.Should().NotContain(l => l.StartsWith("Waiting"));
        lines.Should().NotContain(l => l.Contains("failures"));
    }

    [Fact]
    public void TheSpeedIsLeftOutUntilTheBatchHasRunASecond()
    {
        var young = BatchStatusText.Describe(new AcquireStatus(null, true, 1, 4, 2048, TimeSpan.FromMilliseconds(300), []), "List", new ArchivePlan([]));
        var old = BatchStatusText.Describe(new AcquireStatus(null, true, 1, 4, 2048, TimeSpan.FromSeconds(2), []), "List", new ArchivePlan([]));

        young.Should().Contain(l => l.StartsWith("Downloaded:") && !l.Contains("average"));
        old.Should().Contain(l => l.StartsWith("Downloaded:") && l.Contains("on average") && l.EndsWith("/s"));
    }
}
