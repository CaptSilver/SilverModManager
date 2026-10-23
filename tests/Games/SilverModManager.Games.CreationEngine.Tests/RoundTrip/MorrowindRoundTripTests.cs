using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.LoadOrder.Tests;
using NexusMods.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;
using NexusMods.Games.CreationEngine.Tests.SortOrder;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

/// <summary>
/// Morrowind without OpenMW: the active plugins go into the [Game Files] section of Morrowind.ini and the game
/// loads them in the order of the plugin files' times.
/// </summary>
public class MorrowindRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<MorrowindRoundTripTests, CreationEngine.Morrowind.Morrowind>(helper)
{
    private const string OriginalIni = "[General]\r\nScreen Width=1920\r\n\r\n[Weather]\r\nSunrise Time=6\r\n";

    protected override Version GameVersion => new("1.6.0.1820");
    protected override string PrimaryExe => "Morrowind.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data Files");

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddTempOpenMwConfigRoots();

    // Applies Morrowind, so the production roots would let it rewrite a real openmw.cfg.
    [Fact]
    public void OpenMwConfigRootsAreTempFolders() =>
        ServiceProvider.GetRequiredService<IOpenMwConfigRoots>().Should().BeOfType<TempOpenMwConfigRoots>();

    private AbsolutePath Ini => OnDisk(new GamePath(LocationId.Game, "Morrowind.ini"));

    private static byte[] Plugin() => PluginBytes.Write(PluginFormat.Tes3, new PluginSpec());

    private Task<LoadoutItemGroup.ReadOnly> InstallPlugin(Loadout.ReadOnly loadout, string fileName) =>
        InstallMod(loadout, new Dictionary<RelativePath, byte[]> { { $"Data Files/{fileName}", Plugin() } });

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<string[]> GameFiles() => (await Ini.ReadAllTextAsync())
        .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .Where(l => l.StartsWith("GameFile")).Select(l => l[(l.IndexOf('=') + 1)..]).ToArray();

    private DateTime ModifiedAt(string fileName) => OnDisk(DataPath / fileName).FileInfo.LastWriteTimeUtc;

    [Fact]
    public void TheIniMechanismOrdersByTimestampAndTheMasterFlagCounts()
    {
        Game.PluginList(GameInstallation).Should().BeOfType<MorrowindIniMechanism>().Which.OrdersByTimestamp.Should().BeTrue();
        Game.MasterFlagIgnored(GameInstallation).Should().BeFalse();
    }

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task ActivePluginsReachTheGameFilesSectionAndEverythingElseInTheIniSurvives(string backend)
    {
        var loadout = await CreateLoadoutOn(backend);
        // After the loadout exists: switching a root's backend resets the game and drops the ini the app wrote.
        await Ini.WriteAllBytesAsync(Encoding.Latin1.GetBytes(OriginalIni));

        var first = await InstallPlugin(loadout, "First.esp");
        await Apply(loadout);
        (await GameFiles()).Should().Equal("First.esp");
        (await Ini.ReadAllTextAsync()).Should().StartWith("[General]\r\nScreen Width=1920\r\n").And.Contain("[Weather]\r\nSunrise Time=6\r\n");

        await InstallPlugin(loadout, "Second.esp");
        await Apply(loadout);
        (await GameFiles()).Should().Equal("First.esp", "Second.esp");

        await LoadoutManager.RemoveItems([first.LoadoutItemGroupId]);
        await Apply(loadout);
        (await GameFiles()).Should().Equal("Second.esp");
    }

    [Fact]
    public async Task AMissingIniIsCreatedWithTheSection()
    {
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "First.esp");

        await Apply(loadout);

        (await Ini.ReadAllTextAsync()).Should().Be("[Game Files]\r\nGameFile0=First.esp\r\n");
    }

    [Fact]
    public async Task ASwitchedOffPluginIsAbsentFromTheSection()
    {
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.SetEnabled(variety.SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key("Alpha.esp"), false);
        await Apply(loadout);

        (await GameFiles()).Should().Equal("Beta.esp");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task PluginTimesAscendInTheOrderOfTheSection(string backend)
    {
        var loadout = await CreateLoadoutOn(backend);
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await InstallPlugin(loadout, "Gamma.esp");

        await Apply(loadout);

        var listed = await GameFiles();
        listed.Should().BeEquivalentTo(["Alpha.esp", "Beta.esp", "Gamma.esp"]);
        listed.Select(ModifiedAt).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    // Alphabetical is also the order files are extracted in, so only a move shows the times follow the section.
    [Fact]
    public async Task MovingAPluginRestampsTheFilesInTheNewOrder()
    {
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "Alpha.esp");
        await InstallPlugin(loadout, "Beta.esp");
        await InstallPlugin(loadout, "Gamma.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.MoveItems(variety.SortOrderOf(Refresh(loadout), Connection), [PluginSortOrderTestHelpers.Key("Gamma.esp")],
            PluginSortOrderTestHelpers.Key("Alpha.esp"), TargetRelativePosition.BeforeTarget, Connection.Db);
        await Apply(loadout);

        var listed = await GameFiles();
        listed.Should().Equal("Gamma.esp", "Alpha.esp", "Beta.esp");
        listed.Select(ModifiedAt).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    // Creating the loadout syncs once, and that first sync is the one that seeds.
    [Fact]
    public async Task TheSeedComesFromTheIniAndTheFileTimes()
    {
        var data = OnDisk(DataPath);
        data.CreateDirectory();
        foreach (var name in new[] { "Old.esp", "Newer.esp", "Off.esp" })
            await (data / name).WriteAllBytesAsync(Plugin());
        File.SetLastWriteTimeUtc((data / "Old.esp").ToString(), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc((data / "Off.esp").ToString(), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc((data / "Newer.esp").ToString(), new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Ini.WriteAllTextAsync("[Game Files]\r\nGameFile0=Newer.esp\r\nGameFile1=Old.esp\r\n");

        var loadout = await CreateLoadoutOn("copy");
        await Apply(loadout);

        // The game loads by time, so the ini's own order does not count; Off.esp was not listed, so it stays off.
        (await GameFiles()).Should().Equal("Old.esp", "Newer.esp");
    }
}
