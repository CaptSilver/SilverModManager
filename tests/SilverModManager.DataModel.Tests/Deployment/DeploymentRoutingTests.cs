using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Deployment;

public class DeploymentRoutingTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<DeploymentRoutingTests>(helper)
{
    private static readonly GamePath ModsRoot = new(LocationId.Game, "mods");
    private static readonly GamePath DeepRoot = new(LocationId.Game, "mods/deep");
    private static readonly GamePath OtherRoot = new(LocationId.Game, "other");
    private static readonly GamePath FileA = new(LocationId.Game, "mods/a.txt");
    private static readonly GamePath FileB = new(LocationId.Game, "other/b.txt");
    private static readonly GamePath FileC = new(LocationId.Game, "mods/deep/c.txt");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddRecordingDeploymentBackend();
    }

    private RecordingDeploymentBackend Recording => ServiceProvider.GetRequiredService<RecordingDeploymentBackend>();
    private IDeploymentPlanStore Plans => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    private async Task Route(GameInstallMetadataId metadata, GamePath root, DeploymentBackendId backend)
    {
        using var tx = Connection.BeginTransaction();
        Plans.Write(tx, Connection.Db, metadata, root, backend, chosen: false);
        await tx.Commit();
    }

    private async Task AddMod(Loadout.ReadOnly loadout, string name, params (GamePath Path, string Content)[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        var archived = new List<ArchivedFileEntry>();
        foreach (var (path, content) in files)
        {
            AddFile(tx, loadout.LoadoutId, group, path, content, out var hash, out var size);
            archived.Add(new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(Encoding.UTF8.GetBytes(content))), hash, size));
        }
        await FileStore.BackupFiles(archived);
        await tx.Commit();
    }

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task<string> DescribeGame()
    {
        // A stubbed install starts without a game folder until the first extract.
        var root = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        return root.DirectoryExists() ? await DirectoryDescription.Describe(root) : "";
    }

    [Fact]
    public async Task RoutedRootsGoToTheirBackendAndTheRestToCopy()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        await Route(metadata.Id, ModsRoot, RecordingDeploymentBackend.BackendId);
        await AddMod(loadout, "Routed", (FileA, "a content"), (FileB, "b content"));

        await Synchronizer.Synchronize(loadout);

        Recording.Calls.Should().Contain(("Extract", FileA));
        Recording.Calls.Should().NotContain(c => c.Path == FileB);
        File.ReadAllText(OnDisk(FileA).GetFullPath()).Should().Be("a content");
        File.ReadAllText(OnDisk(FileB).GetFullPath()).Should().Be("b content");
    }

    [Fact]
    public async Task ResetDispatchesThroughThePlan()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        await Route(metadata.Id, ModsRoot, RecordingDeploymentBackend.BackendId);
        await AddMod(loadout, "Routed", (FileA, "a content"), (FileB, "b content"));
        await Synchronizer.Synchronize(loadout);
        Recording.Calls.Clear();

        await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("StubbedGameState.zip")]);

        Recording.Calls.Where(c => c.Phase != "CleanDirectories").Should().Equal(("Delete", FileA));
        Recording.Calls.Where(c => c.Phase == "CleanDirectories").Should().NotBeEmpty();
        OnDisk(FileA).FileExists.Should().BeFalse();
        OnDisk(FileB).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task LongestPrefixRootWins()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        await Route(metadata.Id, ModsRoot, RecordingDeploymentBackend.BackendId);
        await Route(metadata.Id, DeepRoot, DeploymentBackendId.Copy);
        await AddMod(loadout, "Nested", (FileA, "a content"), (FileC, "c content"));

        await Synchronizer.Synchronize(loadout);

        Recording.Calls.Should().Contain(("Extract", FileA));
        Recording.Calls.Should().NotContain(c => c.Path == FileC);
        File.ReadAllText(OnDisk(FileC).GetFullPath()).Should().Be("c content");
    }

    [Fact]
    public async Task UnregisteredBackendFailsBeforeTouchingDisk()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        await Route(metadata.Id, OtherRoot, new DeploymentBackendId("nope"));
        await AddMod(loadout, "Unroutable", (FileA, "a content"), (FileB, "b content"));
        var before = await DescribeGame();

        var act = async () => await Synchronizer.Synchronize(loadout);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*nope*");
        (await DescribeGame()).Should().Be(before);
    }
}
