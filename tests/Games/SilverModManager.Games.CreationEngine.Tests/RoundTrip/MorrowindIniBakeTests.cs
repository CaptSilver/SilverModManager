using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.LoadOrder.Tests;
using SilverModManager.Games.CreationEngine.Morrowind.OpenMw;
using SilverModManager.Games.CreationEngine.Tests.Morrowind.OpenMw;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

/// <summary>
/// Morrowind.ini has one writer: the loadout's INI bake, which renders the [Game Files] section from the plugin
/// order after the baseline, tweaks and the app's keys. The plugin list writer only stands in while the loadout has
/// no copy of the file or leaves the INIs to the game.
/// </summary>
public class MorrowindIniBakeTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<MorrowindIniBakeTests, CreationEngine.Morrowind.Morrowind>(helper)
{
    private const string OriginalIni = "[General]\r\nScreen Width=1920\r\n\r\n[Game Files]\r\nGameFile0=Stale.esp\r\n\r\n[Weather]\r\nSunrise Time=6\r\n";

    private static readonly GamePath IniPath = new(LocationId.Game, "Morrowind.ini");

    protected override Version GameVersion => new("1.6.0.1820");
    protected override string PrimaryExe => "Morrowind.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data Files");

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddTempOpenMwConfigRoots();

    private AbsolutePath Ini => OnDisk(IniPath);

    private static byte[] Plugin() => PluginBytes.Write(PluginFormat.Tes3, new PluginSpec());

    private Task<LoadoutItemGroup.ReadOnly> InstallPlugin(Loadout.ReadOnly loadout, string fileName) =>
        InstallMod(loadout, new Dictionary<RelativePath, byte[]> { { $"Data Files/{fileName}", Plugin() } });

    private async Task Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<IniDocument> LiveIni() => IniDocument.Parse(await Ini.ReadAllBytesAsync());

    private static string[] GameFiles(IniDocument ini) => ini.Entries("Game Files").Select(e => e.Value).ToArray();

    private IIntrinsicFile WriterOfTheIni(Loadout.ReadOnly loadout) =>
        ((ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(Refresh(loadout))[IniPath];

    private async Task<IniDocument> BaselineOf(Loadout.ReadOnly loadout) =>
        IniDocument.Parse(await GameInis.LoadBaseline(FileStore, Refresh(loadout), IniPath));

    [Fact]
    public async Task TheIniOnDiskIsTheBaselineAndThePluginsAreBakedIntoItsGameFilesSection()
    {
        // On disk before the loadout exists, as a player's own file is: the first sync takes it.
        await Ini.WriteAllBytesAsync(Encoding.Latin1.GetBytes(OriginalIni));
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "First.esp");

        await Apply(loadout);

        var state = LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        ((GamePath)state.Path).Should().Be(IniPath);
        state.Baseline.Should().Be(Encoding.Latin1.GetBytes(OriginalIni).xxHash3());
        WriterOfTheIni(loadout).Should().BeOfType<IniBakeIntrinsicFile>("the bake is the only writer of Morrowind.ini");

        var live = await LiveIni();
        GameFiles(live).Should().Equal("First.esp");
        live.Get("General", "Screen Width").Should().Be("1920");
        live.Get("Weather", "Sunrise Time").Should().Be("6");
        // The plugin list is rendered into the file, never into the baseline.
        GameFiles(await BaselineOf(loadout)).Should().Equal("Stale.esp");
    }

    [Fact]
    public async Task APluginChangeIsBakedWithoutTouchingTheBaseline()
    {
        // On disk before the loadout exists, as a player's own file is: the first sync takes it.
        await Ini.WriteAllBytesAsync(Encoding.Latin1.GetBytes(OriginalIni));
        var loadout = await CreateLoadoutOn("copy");
        var first = await InstallPlugin(loadout, "First.esp");
        await Apply(loadout);
        var baseline = LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single().Baseline;

        await InstallPlugin(loadout, "Second.esp");
        await Apply(loadout);
        GameFiles(await LiveIni()).Should().Equal("First.esp", "Second.esp");

        await LoadoutManager.RemoveItems([first.LoadoutItemGroupId]);
        await Apply(loadout);
        GameFiles(await LiveIni()).Should().Equal("Second.esp");

        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single().Baseline.Should().Be(baseline);
    }

    [Fact]
    public async Task APluginOfADisabledModIsLeftOutOfTheGameFilesSection()
    {
        await Ini.WriteAllBytesAsync(Encoding.Latin1.GetBytes(OriginalIni));
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "First.esp");
        var second = await InstallPlugin(loadout, "Second.esp");
        await Apply(loadout);

        await DisableItem(second.Id);
        await Apply(loadout);

        // The plugin's row keeps its own switch on; it is the mod that is off, so the file is not deployed.
        GameFiles(await LiveIni()).Should().Equal("First.esp");
    }

    [Fact]
    public async Task AGameFilesLineTheGameLeftIsNotAdoptedButASettingIs()
    {
        // On disk before the loadout exists, as a player's own file is: the first sync takes it.
        await Ini.WriteAllBytesAsync(Encoding.Latin1.GetBytes(OriginalIni));
        var loadout = await CreateLoadoutOn("copy");
        await InstallPlugin(loadout, "First.esp");
        await Apply(loadout);

        var edited = await LiveIni();
        edited.Set("Game Files", "GameFile5", "Bogus.esp");
        edited.Set("General", "Screen Width", "2560");
        await Ini.WriteAllBytesAsync(edited.ToBytes());
        await Apply(loadout);

        var baseline = await BaselineOf(loadout);
        baseline.Get("General", "Screen Width").Should().Be("2560", "a setting the game changed is the player's choice");
        baseline.Get("Game Files", "GameFile5").Should().BeNull("the plugin list belongs to the app");
        var live = await LiveIni();
        GameFiles(live).Should().Equal("First.esp");
        live.Get("General", "Screen Width").Should().Be("2560");
    }

    [Fact]
    public async Task ALoadoutThatLeavesTheInisToTheGameStillGetsItsPluginList()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.OwnsGameInis, false);
            await tx.Commit();
        }
        await Ini.WriteAllBytesAsync(Encoding.Latin1.GetBytes(OriginalIni));
        await InstallPlugin(loadout, "First.esp");

        await Apply(loadout);

        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        WriterOfTheIni(loadout).Should().BeOfType<PluginsFile>();
        GameFiles(await LiveIni()).Should().Equal("First.esp");
    }
}
