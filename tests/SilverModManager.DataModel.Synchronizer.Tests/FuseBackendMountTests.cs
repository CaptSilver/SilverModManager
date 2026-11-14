using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Deployment.Vfs.Testing;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Mounting a fuse root builds the tree from the rows an apply recorded and the real folder, and finishing it
/// promotes what the game wrote. The kernel is the fake one, so nothing is mounted for real.
/// </summary>
public class FuseBackendMountTests(ITestOutputHelper helper) : AStubbedGameTest<FuseBackendMountTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");
    private static readonly GamePath Vanilla = DataRoot / "vanilla.esp";
    private static readonly GamePath ModEsp = DataRoot / "mod.esp";

    private readonly FakeFuseKernel _kernel = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services).AddUsableFuseProbe().AddFakeProcessTracker();
        services.Replace(ServiceDescriptor.Singleton<IFuseKernel>(_kernel));
        return services;
    }

    private IMountingDeploymentBackend Backend => ServiceProvider.GetServices<IDeploymentBackend>().OfType<IMountingDeploymentBackend>().Single(b => b.Id == DeploymentBackendId.Fuse);

    private GameInstallMetadataId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task<Loadout.ReadOnly> NewLoadoutOnFuse()
    {
        var loadout = await Synchronizer.Synchronize(await CreateLoadout());
        OnDisk(DataRoot).CreateDirectory();
        using var tx = Connection.BeginTransaction();
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, MetadataId, DataRoot, DeploymentBackendId.Fuse, chosen: true);
        await tx.Commit();
        return Refresh(loadout);
    }

    private async Task AddMod(Loadout.ReadOnly loadout, GamePath path, string content, int priority = 1)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
        AddFile(tx, loadout.LoadoutId, group, path, content, out var identity, out var size);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)priority) };
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(Encoding.UTF8.GetBytes(content))), identity, size)]);
        await tx.Commit();
    }

    private string ServedText(string path)
    {
        var file = _kernel.Open(path, VfsOpenFlags.Read).Value;
        try { return Encoding.UTF8.GetString(_kernel.Read(file, 0, 4096).Value); }
        finally { _kernel.Release(file); }
    }

    [Fact]
    public async Task TheMountServesTheModFileAndTheRealFilesTogether()
    {
        var loadout = await NewLoadoutOnFuse();
        await File.WriteAllTextAsync(OnDisk(Vanilla).ToString(), "vanilla bytes");
        await AddMod(loadout, ModEsp, "mod bytes");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        await using var mount = await Backend.Mount(GameInstallation, Refresh(loadout), DataRoot, CancellationToken.None);

        ServedText("mod.esp").Should().Be("mod bytes");
        ServedText("vanilla.esp").Should().Be("vanilla bytes");
        OnDisk(DataRoot / "mod.esp").FileExists.Should().BeFalse("the mod's file exists only in the mount");
        await mount.Finish(CancellationToken.None);
    }

    [Fact]
    public async Task AWritingGameFileIsPromotedIntoTheLoadoutWhenTheMountFinishes()
    {
        var loadout = await NewLoadoutOnFuse();
        await using var mount = await Backend.Mount(GameInstallation, Refresh(loadout), DataRoot, CancellationToken.None);
        var (_, file) = _kernel.Create("saved.txt", 0x1A4, VfsOpenFlags.Read | VfsOpenFlags.Write).Value;
        _kernel.Write(file, 0, Encoding.UTF8.GetBytes("written by the game"));
        _kernel.Release(file);

        var result = await mount.Finish(CancellationToken.None);

        result.Status.Should().Be(MountFinishStatus.Promoted);
        result.Files.Select(f => f.ToString()).Should().Equal("saved.txt");
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath()
            .Select(f => (GamePath)f.TargetPath).Should().Contain(DataRoot / "saved.txt");
    }

    [Fact]
    public async Task RecoverPromotesWhatADeadSessionLeftStagedIntoTheTarget()
    {
        var loadout = await NewLoadoutOnFuse();
        // Where the backend's own session would have staged the game's write before the process died.
        var staged = ServiceProvider.GetRequiredService<VfsSessionFactory>().StagingFor(MetadataId).PathFor(RelativePath.FromUnsanitizedInput("saved.txt"));
        staged.Parent.CreateDirectory();
        await File.WriteAllTextAsync(staged.ToString(), "written before the crash");

        var report = await Backend.Recover(GameInstallation, DataRoot, loadout.LoadoutId, CancellationToken.None);

        report.Status.Should().Be(MountRecoveryStatus.Promoted);
        report.WasMounted.Should().BeFalse();
        report.PromotedFiles.Should().Be(1);
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath()
            .Select(f => (GamePath)f.TargetPath).Should().Contain(DataRoot / "saved.txt");
        (await Backend.Recover(GameInstallation, DataRoot, loadout.LoadoutId, CancellationToken.None)).Status.Should().Be(MountRecoveryStatus.Nothing);
    }
}
