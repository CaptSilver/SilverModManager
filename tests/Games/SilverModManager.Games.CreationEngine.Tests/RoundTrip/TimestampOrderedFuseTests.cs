using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Deployment.Vfs;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Tests;
using SilverModManager.Games.CreationEngine.Tests.OpenMw;
using SilverModManager.Games.CreationEngine.Tests.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

/// <summary>
/// The titles that order their plugins by file time, with the data folder on the fuse backend: the apply records the
/// times on the manifest rows, because a mounted folder is served and no file in it is touched.
/// </summary>
public abstract class ATimestampOrderedFuseTest<TTest, TGame>(ITestOutputHelper helper) : ABethesdaRoundTripTest<TTest, TGame>(helper)
    where TTest : class
    where TGame : class, IGame
{
    private static readonly string[] Plugins = ["Alpha.esp", "Beta.esp", "Gamma.esp"];

    protected abstract byte[] PluginBytes(string fileName);

    /// <summary>Whether a finished apply must leave nothing for the next one to do.</summary>
    protected virtual bool SettlesAfterApply => true;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddUsableFuseProbe();

    private GameInstallMetadataId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    private async Task<Loadout.ReadOnly> LoadoutOnFuse()
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, MetadataId, DataPath, DeploymentBackendId.Fuse, chosen: true);
        await tx.Commit();
        return Refresh(loadout);
    }

    private Task Install(Loadout.ReadOnly loadout, string fileName) =>
        InstallMod(loadout, new Dictionary<RelativePath, byte[]> { { DataPath.Path / fileName, PluginBytes(fileName) } });

    private Task Apply(Loadout.ReadOnly loadout) => Synchronizer.Synchronize(Refresh(loadout));

    // The rows in the order the plugins load.
    private List<(string Name, DateTimeOffset? MTime)> Rows(params string[] order)
    {
        var rows = MountedFileEntry.FindByInstallation(Connection.Db, MetadataId).ToDictionary(r => ((GamePath)r.Path).FileName.ToString());
        return order.Select(name => (name, rows[name].MTime.HasValue ? (DateTimeOffset?)rows[name].MTime.Value : null)).ToList();
    }

    private async Task<bool> HasPendingWork(Loadout.ReadOnly loadout)
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return await Synchronizer.ShouldSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));
    }

    private async Task AssertSettled(Loadout.ReadOnly loadout)
    {
        if (SettlesAfterApply) (await HasPendingWork(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task EveryPluginRowCarriesATimeInLoadOrderAndTheFolderIsUntouched()
    {
        var loadout = await LoadoutOnFuse();
        foreach (var plugin in Plugins) await Install(loadout, plugin);
        var before = Directory.EnumerateFileSystemEntries(OnDisk(DataPath).ToString()).Order().ToList();

        await Apply(loadout);

        var rows = Rows(Plugins);
        rows.Select(r => r.MTime).Should().OnlyContain(t => t.HasValue);
        rows.Select(r => r.MTime!.Value).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        Directory.EnumerateFileSystemEntries(OnDisk(DataPath).ToString()).Order().Should().Equal(before, "nothing is written under a fuse root");
        await AssertSettled(loadout);
    }

    // Alphabetical is also the order rows are recorded in, so only a move shows the times follow the load order.
    [Fact]
    public async Task MovingAPluginRestampsItsRow()
    {
        var loadout = await LoadoutOnFuse();
        foreach (var plugin in Plugins) await Install(loadout, plugin);
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.MoveItems(variety.SortOrderOf(Refresh(loadout), Connection), [PluginSortOrderTestHelpers.Key("Gamma.esp")],
            PluginSortOrderTestHelpers.Key("Alpha.esp"), TargetRelativePosition.BeforeTarget, Connection.Db);
        await Apply(loadout);

        Rows("Gamma.esp", "Alpha.esp", "Beta.esp").Select(r => r.MTime!.Value).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        await AssertSettled(loadout);
    }
}

public class OblivionFuseTimestampTests(ITestOutputHelper helper)
    : ATimestampOrderedFuseTest<OblivionFuseTimestampTests, CreationEngine.Oblivion.Oblivion>(helper)
{
    protected override Version GameVersion => new("1.2.0.416");
    protected override string PrimaryExe => "Oblivion.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override byte[] PluginBytes(string fileName) => BethesdaPluginBytes.Bytes(GameRelease.Oblivion, fileName);
}

public class Fallout3FuseTimestampTests(ITestOutputHelper helper)
    : ATimestampOrderedFuseTest<Fallout3FuseTimestampTests, CreationEngine.Fallout3.Fallout3>(helper)
{
    protected override Version GameVersion => new("1.7.0.3");
    protected override string PrimaryExe => "Fallout3.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override byte[] PluginBytes(string fileName) => BethesdaPluginBytes.Bytes(GameRelease.SkyrimLE, fileName);
}

public class FalloutNewVegasFuseTimestampTests(ITestOutputHelper helper)
    : ATimestampOrderedFuseTest<FalloutNewVegasFuseTimestampTests, CreationEngine.FalloutNewVegas.FalloutNewVegas>(helper)
{
    protected override Version GameVersion => new("1.4.0.525");
    protected override string PrimaryExe => "FalloutNV.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data");
    protected override byte[] PluginBytes(string fileName) => BethesdaPluginBytes.Bytes(GameRelease.SkyrimLE, fileName);
}

public class MorrowindFuseTimestampTests(ITestOutputHelper helper)
    : ATimestampOrderedFuseTest<MorrowindFuseTimestampTests, CreationEngine.Morrowind.Morrowind>(helper)
{
    protected override Version GameVersion => new("1.6.0.1820");
    protected override string PrimaryExe => "Morrowind.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data Files");
    protected override byte[] PluginBytes(string fileName) => Bethesda.LoadOrder.Tests.PluginBytes.Write(PluginFormat.Tes3, new PluginSpec());

    // Morrowind.ini is rewritten on every apply whatever backend holds the data folder (the default backend leaves the
    // same work pending), so only the rows are checked here.
    protected override bool SettlesAfterApply => false;

    // Applies Morrowind, so the production roots would let it rewrite a real openmw.cfg.
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services).AddTempOpenMwConfigRoots();
}
