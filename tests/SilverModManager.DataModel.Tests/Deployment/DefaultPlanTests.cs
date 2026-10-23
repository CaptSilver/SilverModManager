using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Native;

namespace NexusMods.DataModel.Tests.Deployment;

public class DefaultPlanTests(ITestOutputHelper helper) : AppliedGameTest<DefaultPlanTests>(helper)
{
    [Fact(Timeout = TimeoutMs)]
    public async Task AFirstApplyRecordsTheChainsChoiceNotCopy()
    {
        var loadout = await NewLoadoutWithMod();

        await Synchronizer.Synchronize(loadout);

        // Which backend wins depends on the filesystem under the test output, so the test compares with the chain
        // instead of naming one.
        var selector = ServiceProvider.GetRequiredService<IDeploymentBackendSelector>();
        var roots = Plan.Roots;
        roots.Should().NotBeEmpty();
        foreach (var row in roots)
        {
            row.Backend.Should().Be(selector.Choose(GameInstallation, row.Root).Backend, $"the row for {row.Root} is what the chain picks for it");
            row.Chosen.Should().BeFalse("nobody picked it");
            row.Reason.Should().NotBeNullOrEmpty();
        }

        // The saves folder is the one location the chain never deploys into.
        roots.Where(r => r.Root.LocationId != LocationId.Saves).Should().OnlyContain(r => r.Backend != DeploymentBackendId.Copy);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheLoadoutSynchronizerKeepsExistingCopyRows()
    {
        // Only an apply through the synchronizer service moves rows the chain did not pick; see ReselectionTests.
        var loadout = await NewLoadoutWithMod();
        using (var tx = Connection.BeginTransaction())
        {
            // Rows as an install applied before the chain existed has them.
            foreach (var root in DeploymentPlan.Default(GameInstallation.Locations).Roots)
                Plans.Write(tx, Connection.Db, MetadataId, root.Root, DeploymentBackendId.Copy, chosen: false);
            await tx.Commit();
        }

        await Synchronizer.Synchronize(loadout);
        await Synchronizer.Synchronize(loadout);

        Plan.Roots.Should().OnlyContain(r => r.Backend == DeploymentBackendId.Copy);
        foreach (var file in new[] { ModFile, SecondModFile })
        {
            OnDisk(file).FileExists.Should().BeTrue();
            LibcStat.TryLStat(OnDisk(file).ToString(), out var stat).Should().Be(0);
            stat.NLink.Should().Be(1, "a copy is a plain file");
            (stat.Mode & LibcStat.TypeMask).Should().Be(LibcStat.RegularFile);
        }
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task UnManageDeletesThePlanRows()
    {
        var loadout = await NewLoadoutWithMod();
        await Synchronizer.Synchronize(loadout);
        Plan.Roots.Should().NotBeEmpty();

        await SynchronizerService.UnManage(GameInstallation);

        Plan.Roots.Should().BeEmpty("a game managed again must choose its backend again");
    }
}

/// <summary>
/// The temp directory is tmpfs on the dev box, where hard links work and FICLONE does not.
/// </summary>
public class DefaultPlanOnTempDirectoryTests(ITestOutputHelper helper) : AppliedGameTest<DefaultPlanOnTempDirectoryTests>(helper)
{
    private static readonly AbsolutePath Folder = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"default-plan-{Guid.NewGuid():N}");

    protected override AbsolutePath TemporaryFilesPrefix => Folder;

    [Fact(Timeout = TimeoutMs)]
    public async Task AFirstApplyOnTmpfsRecordsHardlinkWithTheReflinkRefusal()
    {
        try
        {
            var loadout = await NewLoadoutWithMod();

            await Synchronizer.Synchronize(loadout);

            var game = Plan.Roots.Single(r => r.Root == GameRoot);
            Assert.SkipWhen(game.Backend == DeploymentBackendId.Reflink, "the temp directory supports FICLONE here");
            game.Backend.Should().Be(DeploymentBackendId.Hardlink);
            game.Reason.Should().Contain("EOPNOTSUPP", "the reflink refusal comes first in the chain's reason");
        }
        finally
        {
            if (System.IO.Directory.Exists(Folder.ToString())) System.IO.Directory.Delete(Folder.ToString(), recursive: true);
        }
    }
}
