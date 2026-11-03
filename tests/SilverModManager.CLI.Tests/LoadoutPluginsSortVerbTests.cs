using FluentAssertions;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout plugins sort</c> runs the LOOT sort the Load Order page offers, with the masterlist the test serves.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutPluginsSortVerbTests(ITestOutputHelper helper) : SkyrimPluginsVerbTest<LoadoutPluginsSortVerbTests>(helper)
{
    private const string BAfterA = """
        plugins:
          - name: 'A.esp'
            after: [ 'B.esp' ]
        """;

    private const string Cycle = """
        plugins:
          - name: 'A.esp'
            after: [ 'B.esp' ]
          - name: 'B.esp'
            after: [ 'A.esp' ]
        """;

    [Fact]
    public async Task Sort_AppliesTheMasterlist()
    {
        var loadout = await LoadoutWithABC();
        Masterlists.Masterlist = BAfterA;
        Masterlists.Revision = "fixture 5";

        var (exit, output) = await Run("loadout", "plugins", "sort", "-l", loadout.ToString());

        exit.Should().Be(0, output);
        output.Should().Contain("fixture 5");
        (await PluginOrder(loadout)).Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task Sort_Cycle_ExitsOneAndLeavesOrder()
    {
        var loadout = await LoadoutWithABC();
        var before = await PluginOrder(loadout);
        Masterlists.Masterlist = Cycle;

        var (exit, output) = await Run("loadout", "plugins", "sort", "-l", loadout.ToString());

        exit.Should().Be(1, output);
        output.Should().Contain("A.esp").And.Contain("B.esp").And.Contain("Cycle");
        (await PluginOrder(loadout)).Should().Equal(before);
    }

    [Fact]
    public async Task Sort_ThenRevert_RestoresTheOrder()
    {
        var loadout = await LoadoutWithABC();
        var before = await PluginOrder(loadout);
        Masterlists.Masterlist = BAfterA;
        await Run("loadout", "plugins", "sort", "-l", loadout.ToString());
        (await PluginOrder(loadout)).Should().NotEqual(before);

        var (exit, output) = await Run("loadout", "revert", "-l", loadout.ToString(), "-r", LatestRevisionNumber(loadout).ToString());

        exit.Should().Be(0, output);
        (await PluginOrder(loadout)).Should().Equal(before);
    }
}
