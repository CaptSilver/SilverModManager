using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.CreationEngine.Models;
using SilverModManager.Games.CreationEngine.Tests.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.RoundTrip;

/// <summary>
/// The titles that write plugins.txt (active plugins) and loadorder.txt (every plugin in order) next to each
/// other: both reach the disk on one sync, list the same plugins, and keep a plugin that is off out of plugins.txt.
/// </summary>
public abstract class ATextfileRoundTripTest<TTest, TGame>(ITestOutputHelper helper) : ACreationEngineRoundTripTest<TTest, TGame>(helper)
    where TTest : class
    where TGame : class, SilverModManager.Abstractions.Games.IGame
{
    protected override string Marker => "";
    protected override GamePath[] CompanionFiles => [LoadOrderFile];

    protected GamePath LoadOrderFile => PluginsFile.Parent / "loadorder.txt";

    /// <summary>Plugins that must lead both files, in order, whatever the mod order.</summary>
    protected virtual string[] LeadingPlugins => [];

    protected async Task<string[]> LoadOrderLines()
    {
        var file = OnDisk(LoadOrderFile);
        file.FileExists.Should().BeTrue();
        return (await file.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task GameHas(GamePath path, string content)
    {
        var file = OnDisk(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
    }

    [Fact]
    public async Task BothFilesAreWrittenWithTheSamePluginsInOrder()
    {
        var loadout = await CreateLoadoutOn("default");
        foreach (var name in LeadingPlugins.Reverse()) await InstallPlugin(loadout, name);
        await InstallPlugin(loadout, "First.esp");
        await Apply(loadout);

        var order = await LoadOrderLines();
        var active = await PluginLines();

        order[0].Should().Be(Header);
        active[0].Should().Be(Header);
        string[] expected = [.. LeadingPlugins, "First.esp"];
        order.Skip(1).Should().Equal(expected);
        active.Skip(1).Should().Equal(expected);
    }

    [Fact]
    public async Task APluginTheLoadoutDoesNotHaveIsInNeitherFile()
    {
        var loadout = await CreateLoadoutOn("default");
        var first = await InstallPlugin(loadout, "First.esp");
        await InstallPlugin(loadout, "Second.esp");
        await Apply(loadout);

        await LoadoutManager.RemoveItems([first.LoadoutItemGroupId]);
        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("Second.esp");
        (await PluginLines()).Skip(1).Should().Equal("Second.esp");
    }

    [Fact]
    public async Task TheFirstApply_TakesTheOrderFromLoadOrderTxtAndTheSwitchesFromPluginsTxt()
    {
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "A.esp");
        await InstallPlugin(loadout, "B.esp");
        await InstallPlugin(loadout, "C.esp");
        var variety = Game.PluginVariety();
        await variety.Unseed(Refresh(loadout), Connection);
        // plugins.txt alone would give C, B: A's place only loadorder.txt knows.
        await GameHas(LoadOrderFile, "C.esp\nA.esp\nB.esp\n");
        await GameHas(PluginsFile, "C.esp\nB.esp\n");

        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("C.esp", "A.esp", "B.esp");
        (await PluginLines()).Skip(1).Should().Equal("C.esp", "B.esp");
    }

    private string[] Notices(Loadout.ReadOnly loadout)
    {
        var id = Game.PluginVariety().SortOrderOf(Refresh(loadout), Connection);
        return Connection.Db.Datoms(PluginListNotice.SortOrder, id)
            .Select(d => PluginListNotice.Load(Connection.Db, d.E).Summary).ToArray();
    }

    private async Task<Loadout.ReadOnly> AppliedLoadoutOfThree()
    {
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "A.esp");
        await InstallPlugin(loadout, "B.esp");
        await InstallPlugin(loadout, "C.esp");
        await Apply(loadout);
        return loadout;
    }

    [Fact]
    public async Task AnEditOfLoadOrderTxt_IsReplacedAndReportedAsMovedPlugins()
    {
        var loadout = await AppliedLoadoutOfThree();

        await GameHas(LoadOrderFile, "C.esp\nA.esp\nB.esp\n");
        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("A.esp", "B.esp", "C.esp");
        var notice = Notices(loadout).Should().ContainSingle().Subject;
        notice.Should().Contain("moved").And.Contain("C.esp").And.NotContain("switched");
    }

    [Fact]
    public async Task AnUntouchedLoadOrderTxt_WithAPluginSwitchedOff_ProducesNoNotice()
    {
        var loadout = await AppliedLoadoutOfThree();
        var variety = Game.PluginVariety();
        await variety.SetEnabled(variety.SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key("B.esp"), false);

        await Apply(loadout);
        await Apply(loadout);

        Notices(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task AnEditOfLoadOrderTxt_DoesNotReportASwitchMadeInTheAppAsTheGames()
    {
        var loadout = await AppliedLoadoutOfThree();
        var variety = Game.PluginVariety();
        await variety.SetEnabled(variety.SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key("B.esp"), false);

        await GameHas(LoadOrderFile, "C.esp\nA.esp\nB.esp\n");
        await Apply(loadout);

        var notice = Notices(loadout).Should().ContainSingle().Subject;
        notice.Should().Contain("moved").And.Contain("C.esp").And.NotContain("switched");
    }

    [Fact]
    public async Task EditsOfBothFiles_AreOneNoticeThatNamesBothChanges()
    {
        var loadout = await AppliedLoadoutOfThree();

        await GameHas(LoadOrderFile, "C.esp\nA.esp\nB.esp\n");
        await GameHas(PluginsFile, "A.esp\nC.esp\n");
        await Apply(loadout);

        var notice = Notices(loadout).Should().ContainSingle().Subject;
        notice.Should().Contain("1 switched off").And.Contain("moved").And.Contain("B.esp").And.Contain("C.esp");
    }

    [Fact]
    public async Task APluginsTxtEdit_IsStillReportedWhenLoadOrderTxtIsUntouched()
    {
        var loadout = await AppliedLoadoutOfThree();

        await GameHas(PluginsFile, "A.esp\nC.esp\n");
        await Apply(loadout);

        Notices(loadout).Should().ContainSingle().Which.Should().Contain("1 switched off").And.Contain("B.esp");
    }

    [Fact]
    public async Task APluginSwitchedOffStaysInLoadOrderTxtAndLeavesPluginsTxt()
    {
        var loadout = await CreateLoadoutOn("default");
        await InstallPlugin(loadout, "A.esp");
        await InstallPlugin(loadout, "B.esp");
        await Apply(loadout);

        var variety = Game.PluginVariety();
        await variety.SetEnabled(variety.SortOrderOf(Refresh(loadout), Connection), PluginSortOrderTestHelpers.Key("A.esp"), false);
        await Apply(loadout);

        (await LoadOrderLines()).Skip(1).Should().Equal("A.esp", "B.esp");
        (await PluginLines()).Skip(1).Should().Equal("B.esp");
    }
}
