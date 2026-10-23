using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Deployment;

public class ChangeDeploymentBackendTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<ChangeDeploymentBackendTests>(helper)
{
    private const int TimeoutMs = 60000;

    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);
    private static readonly GamePath OtherRoot = new(LocationId.Game, "other");
    private static readonly GamePath FileA = new(LocationId.Game, "mods/a.txt");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddRecordingDeploymentBackend();
    }

    private RecordingDeploymentBackend Recording => ServiceProvider.GetRequiredService<RecordingDeploymentBackend>();
    private IDeploymentPlanStore Plans => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    private async Task<Loadout.ReadOnly> ApplyLoadoutWithFile()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
        AddFile(tx, loadout.LoadoutId, group, FileA, "a content", out var hash, out var size);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(FileA.FileName, new MemoryStream(Encoding.UTF8.GetBytes("a content"))), hash, size)]);
        await tx.Commit();

        await Synchronizer.Synchronize(loadout);
        Recording.Calls.Clear();
        return loadout;
    }

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    [Fact(Timeout = TimeoutMs)]
    public async Task SwitchingToANewBackendResetsWithTheOldOneThenReappliesWithTheNewOne()
    {
        await ApplyLoadoutWithFile();

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, RecordingDeploymentBackend.BackendId, chosen: true);

        changed.Should().BeTrue();
        Recording.Calls.Should().NotContain(c => c.Phase == "Delete");
        Recording.Calls.Should().Contain(("Extract", FileA));
        File.ReadAllText(OnDisk(FileA).GetFullPath()).Should().Be("a content");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task SwitchingBackendOnAnUncoveredBuildIsRefusedAndLeavesTheFolderAndRowAlone()
    {
        await ApplyLoadoutWithFile();
        ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).CoversEveryBuild = false;
        var backendBefore = Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId).BackendFor(GameRoot);

        var act = async () => await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, RecordingDeploymentBackend.BackendId, chosen: true);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        Recording.Calls.Should().BeEmpty();
        Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).GameInstallMetadataId).BackendFor(GameRoot).Should().Be(backendBefore);
        File.ReadAllText(OnDisk(FileA).GetFullPath()).Should().Be("a content");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task SwitchingBackToCopyResetsWithTheRecordingBackend()
    {
        await ApplyLoadoutWithFile();
        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, RecordingDeploymentBackend.BackendId, chosen: true);
        Recording.Calls.Clear();

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy, chosen: true);

        changed.Should().BeTrue();
        Recording.Calls.Should().Contain(("Delete", FileA));
        Recording.Calls.Should().NotContain(c => c.Phase == "Extract");
        File.ReadAllText(OnDisk(FileA).GetFullPath()).Should().Be("a content");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task SwitchingToTheCurrentBackendChangesNothing()
    {
        await ApplyLoadoutWithFile();
        var current = Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).BackendFor(GameRoot);

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, current, chosen: true);

        changed.Should().BeFalse();
        Recording.Calls.Should().BeEmpty();
        File.ReadAllText(OnDisk(FileA).GetFullPath()).Should().Be("a content");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AStaleRowNamingAnUnknownBackendCanBeRepaired()
    {
        var loadout = await ApplyLoadoutWithFile();
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        using (var tx = Connection.BeginTransaction())
        {
            Plans.Write(tx, Connection.Db, metadata.Id, OtherRoot, new DeploymentBackendId("nope"), chosen: false);
            await tx.Commit();
        }

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, OtherRoot, DeploymentBackendId.Copy, chosen: true);

        changed.Should().BeTrue();
        await Synchronizer.Synchronize(loadout);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AChosenSwitchMarksTheRow()
    {
        await ApplyLoadoutWithFile();

        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, RecordingDeploymentBackend.BackendId, chosen: true);

        Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).Roots
            .Single(r => r.Root == GameRoot).Chosen.Should().BeTrue();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AChainDrivenSwitchLeavesTheRowUnmarked()
    {
        await ApplyLoadoutWithFile();
        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, RecordingDeploymentBackend.BackendId, chosen: true);

        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy, chosen: false);

        var root = Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).Roots.Single(r => r.Root == GameRoot);
        root.Backend.Should().Be(DeploymentBackendId.Copy);
        root.Chosen.Should().BeFalse();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ChoosingTheBackendARootAlreadyUsesMarksTheRowWithoutRedeploying()
    {
        await ApplyLoadoutWithFile();
        var current = Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).BackendFor(GameRoot);

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, current, chosen: true);

        changed.Should().BeFalse("nothing is deployed differently");
        Recording.Calls.Should().BeEmpty();
        var root = Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).Roots.Single(r => r.Root == GameRoot);
        root.Backend.Should().Be(current);
        root.Chosen.Should().BeTrue("a person's pick keeps the selection chain from moving the root at the next apply");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AChainDrivenRequestForTheCurrentBackendDoesNotMarkTheRow()
    {
        await ApplyLoadoutWithFile();
        var current = Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).BackendFor(GameRoot);

        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, current, chosen: false);

        Plans.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).Roots
            .Single(r => r.Root == GameRoot).Chosen.Should().BeFalse();
    }
}
