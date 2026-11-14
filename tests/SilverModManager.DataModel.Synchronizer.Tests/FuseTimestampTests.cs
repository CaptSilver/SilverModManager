using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Vfs.Testing;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Abstractions.Games;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A game that orders its plugins by file time can use a mounted folder: the apply records the planned time on the
/// manifest row (the mount serves it) and touches no file under the root.
/// </summary>
[Collection(TimestampStubbedGame.Collection)]
public class FuseTimestampTests : AIsolatedGameTest<FuseTimestampTests, TimestampStubbedGame>
{
    private readonly FakeFuseKernel _kernel = new();

    private static readonly DateTimeOffset T0 = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GamePath PluginsRoot = new(LocationId.Game, "plugins");
    private static readonly GamePath A = PluginsRoot / "a.esp";
    private static readonly GamePath B = PluginsRoot / "b.esp";
    private static readonly GamePath C = PluginsRoot / "c.esp";

    public FuseTimestampTests(ITestOutputHelper helper) : base(helper) => TimestampStubbedGame.Order = [];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<TimestampStubbedGame>()
            .AddUniversalGameLocator<TimestampStubbedGame>(Version.Parse("0.0.0"))
            .AddUsableFuseProbe()
            .AddFakeProcessTracker();
        services.Replace(ServiceDescriptor.Singleton<IFuseKernel>(_kernel));
        return services;
    }

    private IMountingDeploymentBackend Backend => ServiceProvider.GetServices<IDeploymentBackend>().OfType<IMountingDeploymentBackend>().Single(b => b.Id == DeploymentBackendId.Fuse);

    private GameInstallMetadataId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private void StampWith(params (GamePath Path, int Minutes)[] order) =>
        TimestampStubbedGame.Order = order.Select(o => (o.Path, T0.AddMinutes(o.Minutes))).ToList();

    private async Task<Loadout.ReadOnly> LoadoutOnFuse()
    {
        // The first sync of a loadout is a switch, which takes whatever is on disk as the loadout's own baseline.
        var loadout = await Synchronizer.Synchronize(await CreateLoadout());
        OnDisk(PluginsRoot).CreateDirectory();
        using var tx = Connection.BeginTransaction();
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, MetadataId, PluginsRoot, DeploymentBackendId.Fuse, chosen: true);
        await tx.Commit();
        return Refresh(loadout);
    }

    private async Task AddPlugins(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, ((GamePath[])[A, B, C]).Select(p => (RelativePath)p.Path), loadout.LoadoutId, "plugins");
        await tx.Commit();
    }

    private Dictionary<GamePath, DateTimeOffset?> RowTimes() =>
        MountedFileEntry.FindByInstallation(Connection.Db, MetadataId)
            .ToDictionary(r => (GamePath)r.Path, r => r.MTime.HasValue ? (DateTimeOffset?)r.MTime.Value : null);

    private async Task<bool> HasPendingWork(Loadout.ReadOnly loadout)
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return await Synchronizer.ShouldSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));
    }

    [Fact]
    public async Task AnAppliedFileCarriesItsPlannedTimeOnItsRow()
    {
        var loadout = await LoadoutOnFuse();
        await AddPlugins(loadout);
        StampWith((A, 0), (B, 1), (C, 2));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        RowTimes().Should().BeEquivalentTo(new Dictionary<GamePath, DateTimeOffset?> { [A] = T0, [B] = T0.AddMinutes(1), [C] = T0.AddMinutes(2) });
        Directory.EnumerateFileSystemEntries(OnDisk(PluginsRoot).ToString()).Should().BeEmpty("nothing is written under a fuse root");
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AFileAlreadyServedIsRestampedWhenTheOrderChanges()
    {
        var loadout = await LoadoutOnFuse();
        await AddPlugins(loadout);
        StampWith((A, 0), (B, 1), (C, 2));
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        StampWith((C, 0), (B, 1), (A, 2));
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        RowTimes().Should().BeEquivalentTo(new Dictionary<GamePath, DateTimeOffset?> { [C] = T0, [B] = T0.AddMinutes(1), [A] = T0.AddMinutes(2) });
        MountedFileEntry.FindByInstallation(Connection.Db, MetadataId).Should().HaveCount(3, "a restamp updates the rows, it does not add any");
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    // The real folder already holds the mod's bytes, so no row is needed to serve them; one is still needed to serve the time.
    [Fact]
    public async Task AFileTheRealFolderAlreadyHoldsStillGetsItsTime()
    {
        var loadout = await LoadoutOnFuse();
        await OnDisk(A).WriteAllTextAsync(A.Path.ToString());
        File.SetLastWriteTimeUtc(OnDisk(A).ToString(), T0.AddDays(-30).UtcDateTime);
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)A.Path], loadout.LoadoutId, "plugins");
            await tx.Commit();
        }
        StampWith((A, 5));
        var realBefore = File.GetLastWriteTimeUtc(OnDisk(A).ToString());

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        RowTimes().Should().BeEquivalentTo(new Dictionary<GamePath, DateTimeOffset?> { [A] = T0.AddMinutes(5) });
        File.GetLastWriteTimeUtc(OnDisk(A).ToString()).Should().Be(realBefore, "the real file's time is not touched");
        (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task AGameThatNamesNoTimesLeavesTheRowsUnstamped()
    {
        var loadout = await LoadoutOnFuse();
        await AddPlugins(loadout);

        await Synchronizer.Synchronize(Refresh(loadout));

        RowTimes().Values.Should().OnlyContain(t => t == null);
    }

    // A plugin that was in the folder before the loadout, like a vanilla master: nothing extracts it, so the stamp adds
    // the row, and the mount serves the real file under the planned time without staging a copy of it.
    [Fact]
    public async Task APluginTheRealFolderHeldFirstIsServedFromItWithItsPlannedTime()
    {
        OnDisk(PluginsRoot).CreateDirectory();
        await OnDisk(A).WriteAllTextAsync("vanilla bytes");
        var loadout = await LoadoutOnFuse();
        StampWith((A, 5));

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        RowTimes().Should().BeEquivalentTo(new Dictionary<GamePath, DateTimeOffset?> { [A] = T0.AddMinutes(5) });
        (await HasPendingWork(loadout)).Should().BeFalse();
        await AssertServed(loadout, "vanilla bytes", T0.AddMinutes(5));
    }

    [Fact]
    public async Task APluginServedAgainFromTheRealFolderKeepsItsPlannedTime()
    {
        OnDisk(PluginsRoot).CreateDirectory();
        await OnDisk(A).WriteAllTextAsync("vanilla bytes");
        var loadout = await LoadoutOnFuse();
        LoadoutItemGroupId mod;
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
            AddFile(tx, loadout.LoadoutId, group, A, "mod bytes", out var identity, out var size);
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(A.FileName, new MemoryStream("mod bytes"u8.ToArray())), identity, size)]);
            mod = LoadoutItemGroupId.From((await tx.Commit())[group.Value]);
        }
        StampWith((A, 5));
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        await DisableItem(mod.Value);
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        RowTimes().Should().BeEquivalentTo(new Dictionary<GamePath, DateTimeOffset?> { [A] = T0.AddMinutes(5) });
        (await HasPendingWork(loadout)).Should().BeFalse();
        await AssertServed(loadout, "vanilla bytes", T0.AddMinutes(5));
    }

    private async Task AssertServed(Loadout.ReadOnly loadout, string text, DateTimeOffset mtime)
    {
        var staged = ServiceProvider.GetRequiredService<ILooseStagingStore>().Enumerate(MetadataId).Select(s => s.Identity).ToList();
        var realTime = File.GetLastWriteTimeUtc(OnDisk(A).ToString());
        await using var mount = await Backend.Mount(GameInstallation, Refresh(loadout), PluginsRoot, CancellationToken.None);

        _kernel.Resolve("a.esp").Value.Attr.MTime.Should().Be(mtime);
        var file = _kernel.Open("a.esp", VfsOpenFlags.Read).Value;
        try { Encoding.UTF8.GetString(_kernel.Read(file, 0, 4096).Value).Should().Be(text); }
        finally { _kernel.Release(file); }
        ServiceProvider.GetRequiredService<ILooseStagingStore>().Enumerate(MetadataId).Select(s => s.Identity).Should()
            .BeEquivalentTo(staged, "the real folder already holds the bytes");
        File.GetLastWriteTimeUtc(OnDisk(A).ToString()).Should().Be(realTime, "the real file's time is not touched");
        await mount.Finish(CancellationToken.None);
    }
}
