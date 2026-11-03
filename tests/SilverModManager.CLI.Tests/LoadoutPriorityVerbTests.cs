using System.Text.Json.Nodes;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout priority list</c> and <c>loadout group priority get/set</c> read and move the mod priority order the
/// Load Order page shows for a game with no plugin order. Index 0 is the lowest priority; the last row wins.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutPriorityVerbTests(ITestOutputHelper helper) : EldenRingVerbTest<LoadoutPriorityVerbTests>(helper)
{
    private async Task<LoadoutId> LoadoutWithMods(params string[] namesLowestFirst)
    {
        var loadout = await CreateLoadout();
        ulong priority = 0;
        foreach (var name in namesLowestFirst)
        {
            var mod = await AddMod(loadout.LoadoutId, name);
            using var tx = Connection.BeginTransaction();
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = mod, Priority = ConflictPriority.From(++priority) };
            await tx.Commit();
        }
        return loadout.LoadoutId;
    }

    private string[] StoredOrder(LoadoutId loadout) => LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout)
        .OrderBy(row => row.Priority)
        .Select(row => row.Target.AsLoadoutItem().Name)
        .ToArray();

    [Fact]
    public async Task List_PrintsStoredOrderWinnerLast()
    {
        var loadout = await LoadoutWithMods("Alpha", "Beta", "Gamma");

        var (exit, output) = await Run("loadout", "priority", "list", "-l", loadout.ToString());

        exit.Should().Be(0, output);
        output.IndexOf("Alpha", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Beta", StringComparison.Ordinal));
        output.IndexOf("Beta", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Gamma", StringComparison.Ordinal));
        output.Should().ContainEquivalentOf("last loaded wins");
    }

    [Fact]
    public async Task Get_ReportsTheGroupsIndex()
    {
        var loadout = await LoadoutWithMods("Alpha", "Beta", "Gamma");

        var (exit, output) = await Run("loadout", "group", "priority", "get", "-l", loadout.ToString(), "-g", "Beta", "--json");

        exit.Should().Be(0, output);
        var row = JsonNode.Parse(output)!.AsObject();
        ((int)row["index"]!).Should().Be(1);
        ((string?)row["name"]).Should().Be("Beta");
    }

    [Fact]
    public async Task Set_MovesTheGroupToTheIndex()
    {
        var loadout = await LoadoutWithMods("Alpha", "Beta", "Gamma");

        var (exit, output) = await Run("loadout", "group", "priority", "set", "-l", loadout.ToString(), "-g", "Alpha", "--index", "2");

        exit.Should().Be(0, output);
        StoredOrder(loadout).Should().Equal("Beta", "Gamma", "Alpha");
    }

    [Fact]
    public async Task Set_MovesTowardsTheLowEnd()
    {
        var loadout = await LoadoutWithMods("Alpha", "Beta", "Gamma");

        var (exit, output) = await Run("loadout", "group", "priority", "set", "-l", loadout.ToString(), "-g", "Gamma", "--index", "0");

        exit.Should().Be(0, output);
        StoredOrder(loadout).Should().Equal("Gamma", "Alpha", "Beta");
    }

    [Fact]
    public async Task Set_ClampsAnOutOfRangeIndex()
    {
        var loadout = await LoadoutWithMods("Alpha", "Beta", "Gamma");

        var (exit, output) = await Run("loadout", "group", "priority", "set", "-l", loadout.ToString(), "-g", "Alpha", "--index", "99");

        exit.Should().Be(0, output);
        output.Should().Contain("clamped");
        StoredOrder(loadout).Should().Equal("Beta", "Gamma", "Alpha");
    }

    [Fact]
    public async Task Set_ThenRevert_RestoresTheOrder()
    {
        var loadout = await LoadoutWithMods("Alpha", "Beta", "Gamma");
        await Run("loadout", "group", "priority", "set", "-l", loadout.ToString(), "-g", "Alpha", "--index", "2");
        StoredOrder(loadout).Should().Equal("Beta", "Gamma", "Alpha");

        var (exit, output) = await Run("loadout", "revert", "-l", loadout.ToString(), "-r", LatestRevisionNumber(loadout).ToString());

        exit.Should().Be(0, output);
        StoredOrder(loadout).Should().Equal("Alpha", "Beta", "Gamma");
    }

    [Fact]
    public async Task Json_RowsCarryIndexIdNameEnabled()
    {
        var loadout = await LoadoutWithMods("Alpha", "Beta");
        await Run("loadout", "group", "disable", "-l", loadout.ToString(), "-g", "Beta");

        var (exit, output) = await Run("loadout", "priority", "list", "-l", loadout.ToString(), "--json");

        exit.Should().Be(0, output);
        var rows = JsonNode.Parse(output)!.AsArray().Select(n => n!.AsObject()).ToArray();
        rows.Select(r => (string?)r["name"]).Should().Equal("Alpha", "Beta");
        rows.Select(r => (int)r["index"]!).Should().Equal(0, 1);
        rows.Select(r => (bool)r["isEnabled"]!).Should().Equal(true, false);
        ((string?)rows[0]["groupId"]).Should().StartWith("EId:");
    }

    [Fact]
    public async Task PluginsList_OnAGameWithoutPlugins_ExitsOneWithAPlainMessage()
    {
        var loadout = await LoadoutWithMods("Alpha");

        var (exit, output) = await Run("loadout", "plugins", "list", "-l", loadout.ToString());

        exit.Should().Be(1);
        output.Should().Contain("This game has no plugin order.");
    }
}
