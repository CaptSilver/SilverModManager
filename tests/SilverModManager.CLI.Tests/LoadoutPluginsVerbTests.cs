using System.Text.Json.Nodes;
using FluentAssertions;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout plugins list</c> and <c>loadout plugins set</c> read and move the plugin order through the sort order
/// variety the Load Order page drags in.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutPluginsVerbTests(ITestOutputHelper helper) : SkyrimPluginsVerbTest<LoadoutPluginsVerbTests>(helper)
{
    [Fact]
    public async Task List_PrintsPluginsInOrder()
    {
        var loadout = await LoadoutWithABC();

        var (exit, output) = await Run("loadout", "plugins", "list", "-l", loadout.ToString());

        exit.Should().Be(0, output);
        var positions = new[] { "Skyrim.esm", "A.esp", "B.esp", "C.esp" }.Select(name => output.IndexOf(name, StringComparison.Ordinal)).ToArray();
        positions.Should().OnlyContain(p => p >= 0).And.BeInAscendingOrder();
        output.Should().ContainEquivalentOf("last loaded wins");
    }

    [Fact]
    public async Task List_Json_CarriesTheRowFields()
    {
        var loadout = await LoadoutWithABC();

        var (exit, output) = await Run("loadout", "plugins", "list", "-l", loadout.ToString(), "--json");

        exit.Should().Be(0, output);
        var rows = JsonNode.Parse(output)!.AsArray().Select(n => n!.AsObject()).ToArray();
        rows.Select(r => (string?)r["name"]).Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");
        rows.Select(r => (int)r["index"]!).Should().BeInAscendingOrder();
        ((bool)rows[0]["isLocked"]!).Should().BeTrue("the game always loads Skyrim.esm first");
        ((bool)rows[1]["isEnabled"]!).Should().BeTrue();
        ((string?)rows[1]["mod"]).Should().Be("Mods");
        rows[1]["masters"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Set_ReordersTheNamedPluginsOnly()
    {
        var loadout = await LoadoutWithABC();

        // C keeps its place, A goes right after it, and B is not named so it stays where it was relative to the rest.
        var (exit, output) = await Run("loadout", "plugins", "set", "-l", loadout.ToString(), "-o", "C.esp;A.esp");

        exit.Should().Be(0, output);
        (await PluginOrder(loadout)).Should().Equal("Skyrim.esm", "B.esp", "C.esp", "A.esp");
    }

    [Fact]
    public async Task Set_UnknownName_WritesNothing()
    {
        var loadout = await LoadoutWithABC();
        var before = await PluginOrder(loadout);
        var revisions = LatestRevisionNumber(loadout);

        var (exit, output) = await Run("loadout", "plugins", "set", "-l", loadout.ToString(), "-o", "C.esp;Nope.esp");

        exit.Should().Be(1);
        output.Should().Contain("Nope.esp");
        (await PluginOrder(loadout)).Should().Equal(before);
        LatestRevisionNumber(loadout).Should().Be(revisions, "a refused command leaves no restore point");
    }

    [Fact]
    public async Task Set_LockedPlugin_IsRefused()
    {
        var loadout = await LoadoutWithABC();
        var before = await PluginOrder(loadout);

        var (exit, output) = await Run("loadout", "plugins", "set", "-l", loadout.ToString(), "-o", "A.esp;Skyrim.esm");

        exit.Should().Be(1);
        output.Should().Contain("Skyrim.esm").And.Contain("can't be moved");
        (await PluginOrder(loadout)).Should().Equal(before);
    }

    [Fact]
    public async Task Set_ThenRevert_RestoresTheOrder()
    {
        var loadout = await LoadoutWithABC();
        var before = await PluginOrder(loadout);
        await Run("loadout", "plugins", "set", "-l", loadout.ToString(), "-o", "C.esp;A.esp");
        (await PluginOrder(loadout)).Should().NotEqual(before);

        var (exit, output) = await Run("loadout", "revert", "-l", loadout.ToString(), "-r", LatestRevisionNumber(loadout).ToString());

        exit.Should().Be(0, output);
        (await PluginOrder(loadout)).Should().Equal(before);
    }
}
