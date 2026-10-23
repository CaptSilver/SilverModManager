using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using R3;
using Xunit;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.DataModel.Synchronizer.Tests;

public class ApplyProgressTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ApplyProgressTests>(helper)
{
    private static readonly string[] ExternalFiles = ["bin/progress/a.txt", "bin/progress/b.txt", "bin/progress/c.txt"];

    private async Task<List<SyncProgress>> ApplyRecording(Loadout.ReadOnly loadout)
    {
        var job = new SynchronizeLoadoutJob(loadout.LoadoutId);
        var seen = new List<SyncProgress>();
        using var _ = job.Progress.Subscribe(seen.Add);
        await Synchronizer.Synchronize(loadout, job);
        return seen;
    }

    [Fact]
    public async Task Collecting_ReportsFilesDoneOutOfTheTotal_WhichReachesTheTotal()
    {
        // Created after the loadout: its first apply would otherwise take the files in already.
        var loadout = await CreateLoadout();
        foreach (var file in ExternalFiles)
        {
            var path = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, file));
            path.Parent.CreateDirectory();
            await path.WriteAllTextAsync("content of " + file);
        }

        var seen = await ApplyRecording(loadout);

        var collecting = seen.Where(p => p.Phase == SyncPhases.Collecting && p.FilesTotal.HasValue).ToList();
        collecting.Should().NotBeEmpty("collecting knows how many files it has to look at");
        collecting.Last().FilesTotal.Should().BeGreaterThanOrEqualTo(ExternalFiles.Length);
        collecting.Last().FilesDone.Should().Be(collecting.Last().FilesTotal!.Value);
    }

    [Fact]
    public async Task EveryPhase_ReportsNonDecreasingProgress_AgainstAFixedTotal()
    {
        // Created after the loadout: its first apply would otherwise take the files in already.
        var loadout = await CreateLoadout();
        foreach (var file in ExternalFiles)
        {
            var path = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, file));
            path.Parent.CreateDirectory();
            await path.WriteAllTextAsync("content of " + file);
        }

        var seen = await ApplyRecording(loadout);

        foreach (var phase in seen.GroupBy(p => p.Phase))
        {
            var reports = phase.ToList();
            reports.Select(p => p.FilesDone).Should().BeInAscendingOrder($"{phase.Key} never moves backwards");
            reports.Select(p => p.BytesDone).Should().BeInAscendingOrder($"{phase.Key} never moves backwards");
            reports.Where(p => p.FilesTotal.HasValue).Select(p => p.FilesTotal).Distinct().Should().HaveCountLessThanOrEqualTo(1, $"{phase.Key} total does not change while it runs");
        }
    }

    [Fact]
    public async Task BackingUp_ReportsTheFilesAndBytesItHasToBackUp()
    {
        var loadout = await CreateLoadout();
        var sizes = new List<long>();
        foreach (var file in ExternalFiles)
        {
            var path = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, file));
            path.Parent.CreateDirectory();
            var text = "content of " + file;
            await path.WriteAllTextAsync(text);
            sizes.Add(text.Length);
        }

        var seen = await ApplyRecording(loadout);

        var backup = seen.Where(p => p.Phase == SyncPhases.BackingUp).ToList();
        backup.Should().NotBeEmpty();
        backup.Last().FilesTotal.Should().BeGreaterThanOrEqualTo(ExternalFiles.Length);
        backup.Last().FilesDone.Should().Be(backup.Last().FilesTotal!.Value);
        backup.Last().BytesTotal.Should().BeGreaterThanOrEqualTo(sizes.Sum());
        backup.Last().BytesDone.Should().Be(backup.Last().BytesTotal!.Value);
    }

    [Fact]
    public async Task Deploying_ReportsFilesDoneOutOfTheFilesItPlaces()
    {
        var loadout = await CreateLoadout();
        List<RelativePath> modFiles = ["bin/mods/progress/one.txt", "bin/mods/progress/two.txt", "bin/mods/progress/three.txt"];
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, modFiles, loadout, "ProgressMod");
            await tx.Commit();
        }
        Refresh(ref loadout);

        var seen = await ApplyRecording(loadout);

        var deploying = seen.Where(p => p.Phase == SyncPhases.Deploying).ToList();
        deploying.Should().NotBeEmpty();
        deploying.Last().FilesTotal.Should().Be(modFiles.Count);
        deploying.Last().FilesDone.Should().Be(modFiles.Count);
    }

    [Fact]
    public void APhaseRunAgainForAnotherRoot_ReportsFromItsOwnStart()
    {
        var job = new SynchronizeLoadoutJob(SilverModManager.Sdk.Loadouts.LoadoutId.From(1UL));
        job.ReportProgress(SyncPhases.Deploying, 3, 3);
        job.ReportProgress(SyncPhases.Deploying, 0, 5);

        job.Progress.Value.Should().Be(new SyncProgress(SyncPhases.Deploying, 0, 5, 0, null));
    }

    [Fact]
    public void AnUnknownTotal_IsIndeterminate_AndAKnownOneIsAFraction()
    {
        new SyncProgress(SyncPhases.Collecting, 5, null, 0, null).Fraction.Should().BeNull();
        new SyncProgress(SyncPhases.Collecting, 5, 10, 0, null).Fraction.Should().Be(0.5);
        new SyncProgress(SyncPhases.BackingUp, 1, 10, 750, 1000).Fraction.Should().Be(0.75, "bytes are the truer measure when both are known");
        new SyncProgress(SyncPhases.Deploying, 0, 0, 0, null).Fraction.Should().BeNull("nothing to do has no fraction");
    }
}
