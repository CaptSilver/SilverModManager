using System.Reactive.Subjects;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.UI.Tests.Pages;

public class WabbajackInstallStatusFeedTests
{
    private static ModlistInstallProgress.Snapshot Writing(int done, int total)
        => ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = done, TotalItems = total };

    [Fact]
    public void EachSnapshotBecomesTheTextAndTheBar()
    {
        var snapshots = new Subject<ModlistInstallProgress.Snapshot>();
        var seen = new List<WabbajackInstallStatus>();
        using var _ = WabbajackInstallStatusFeed.Follow(snapshots).Subscribe(seen.Add);

        snapshots.OnNext(Writing(0, 600));
        snapshots.OnNext(Writing(59, 600));
        snapshots.OnNext(Writing(60, 600));

        seen.Should().Equal(
            new WabbajackInstallStatus("Writing files: 0 of 600", 0),
            new WabbajackInstallStatus("Writing files: 59 of 600", 59.0 / 600),
            new WabbajackInstallStatus("Writing files: 60 of 600", 0.1));
    }
}
