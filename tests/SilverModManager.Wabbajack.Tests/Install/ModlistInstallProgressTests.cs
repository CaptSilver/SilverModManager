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
}
