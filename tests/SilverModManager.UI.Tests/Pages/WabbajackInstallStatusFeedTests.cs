using System.Reactive.Subjects;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackInstallStatusFeedTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ModlistInstallProgress.Snapshot Writing(int done, int total)
        => ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = done, TotalItems = total };

    [Fact]
    public void EachSnapshotBecomesTheTextTheBarAndAnEtaThatIsEmptyUntilAMinuteOfData()
    {
        var time = new ManualTime();
        var snapshots = new Subject<ModlistInstallProgress.Snapshot>();
        var seen = new List<WabbajackInstallStatus>();
        using var _ = WabbajackInstallStatusFeed.Follow(snapshots, time).Subscribe(seen.Add);

        snapshots.OnNext(Writing(0, 600));
        time.Now = time.Now.AddSeconds(59);
        snapshots.OnNext(Writing(59, 600));
        time.Now = time.Now.AddSeconds(1);
        snapshots.OnNext(Writing(60, 600));

        seen.Should().Equal(
            new WabbajackInstallStatus("Writing files: 0 of 600", 0, ""),
            new WabbajackInstallStatus("Writing files: 59 of 600", 59.0 / 600, ""),
            new WabbajackInstallStatus("Writing files: 60 of 600", 0.1, "about 9 min left"));
    }

    [Fact]
    public void EverySubscriptionEstimatesFromItsOwnStart()
    {
        var time = new ManualTime();
        var snapshots = new Subject<ModlistInstallProgress.Snapshot>();
        var feed = WabbajackInstallStatusFeed.Follow(snapshots, time);
        using var early = feed.Subscribe(_ => { });
        snapshots.OnNext(Writing(0, 600));
        time.Now = time.Now.AddMinutes(2);
        snapshots.OnNext(Writing(120, 600));

        var late = new List<WabbajackInstallStatus>();
        using var _ = feed.Subscribe(late.Add);
        snapshots.OnNext(Writing(121, 600));

        late.Should().ContainSingle().Which.EtaText.Should().BeEmpty("the late page has seen no time yet");
    }
}
