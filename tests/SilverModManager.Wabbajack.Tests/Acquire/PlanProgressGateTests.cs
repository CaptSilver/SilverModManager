using FluentAssertions;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class PlanProgressGateTests
{
    private sealed class Reports : IProgress<PlanProgress>
    {
        public List<PlanProgress> All { get; } = [];
        public void Report(PlanProgress value) => All.Add(value);
    }

    [Fact]
    public void AHotLoopReachesTheSinkAFewTimesButNeverLosesTheStartOrTheEnd()
    {
        var sink = new Reports();
        var gate = new PlanProgressGate(sink);

        for (var i = 0; i <= 10_000; i++) gate.Report(new PlanProgress(PlanPhase.CheckingLibrary, i, 10_000));

        sink.All.Should().HaveCountLessThan(100);
        sink.All.First().Done.Should().Be(0);
        sink.All.Last().Done.Should().Be(10_000);
    }

    [Fact]
    public void ANewPhaseAlwaysGoesThrough()
    {
        var sink = new Reports();
        var gate = new PlanProgressGate(sink);

        gate.Report(new PlanProgress(PlanPhase.CheckingLibrary, 1, 5));
        gate.Report(new PlanProgress(PlanPhase.HashingGameFiles, 0, 100, "a.bsa"));

        sink.All.Select(report => report.Phase).Should().Equal(PlanPhase.CheckingLibrary, PlanPhase.HashingGameFiles);
    }

    [Fact]
    public void WithoutASinkReportingDoesNothing()
    {
        var gate = new PlanProgressGate(null);

        gate.Invoking(g => g.Report(new PlanProgress(PlanPhase.ReadingList))).Should().NotThrow();
    }
}
