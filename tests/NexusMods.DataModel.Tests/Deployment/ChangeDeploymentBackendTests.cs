using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.DataModel.Tests.Deployment;

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

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, RecordingDeploymentBackend.BackendId);

        changed.Should().BeTrue();
        Recording.Calls.Should().NotContain(c => c.Phase == "Delete");
        Recording.Calls.Should().Contain(("Extract", FileA));
        File.ReadAllText(OnDisk(FileA).GetFullPath()).Should().Be("a content");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task SwitchingBackToCopyResetsWithTheRecordingBackend()
    {
        await ApplyLoadoutWithFile();
        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, RecordingDeploymentBackend.BackendId);
        Recording.Calls.Clear();

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy);

        changed.Should().BeTrue();
        Recording.Calls.Should().Contain(("Delete", FileA));
        Recording.Calls.Should().NotContain(c => c.Phase == "Extract");
        File.ReadAllText(OnDisk(FileA).GetFullPath()).Should().Be("a content");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task SwitchingToTheCurrentBackendChangesNothing()
    {
        await ApplyLoadoutWithFile();

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy);

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
            Plans.Write(tx, metadata.Id, OtherRoot, new DeploymentBackendId("nope"));
            await tx.Commit();
        }

        var changed = await SynchronizerService.ChangeDeploymentBackend(GameInstallation, OtherRoot, DeploymentBackendId.Copy);

        changed.Should().BeTrue();
        await Synchronizer.Synchronize(loadout);
    }
}
