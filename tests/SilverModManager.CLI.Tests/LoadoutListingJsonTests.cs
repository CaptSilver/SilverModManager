using System.Text.Json.Nodes;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The JSON of <c>loadout group list</c> and <c>loadout revisions</c>, which the MCP tools read, and the way
/// <c>loadout group list</c> treats a group that does not exist.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutListingJsonTests(ITestOutputHelper helper) : EldenRingVerbTest<LoadoutListingJsonTests>(helper)
{
    [Fact]
    public async Task GroupList_Json_CarriesFolderFileAndHash_AndNullHashForAPathOnlyItem()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout.LoadoutId, "Plain Mod");
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutItemWithTargetPath.New(tx, out var id)
            {
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, ParentId = mod, Name = "no content" },
                TargetPath = new GamePath(LocationId.Game, "no_content.txt").ToGamePathParentTuple(loadout.LoadoutId),
            };
            await tx.Commit();
        }

        var (exit, output) = await Run("loadout", "group", "list", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod", "--json");

        exit.Should().Be(0, output);
        var rows = JsonNode.Parse(output)!.AsArray().Select(n => n!.AsObject()).ToArray();
        rows.Should().HaveCount(2);
        var withFile = rows.Single(r => ((string?)r["file"])!.EndsWith("file.txt"));
        ((string?)withFile["hash"]).Should().NotBeNullOrEmpty().And.NotBe("<none>");
        ((string?)withFile["folder"]).Should().NotBeNullOrEmpty();
        var pathOnly = rows.Single(r => (string?)r["file"] == "no_content.txt");
        pathOnly["hash"].Should().BeNull("the '<none>' placeholder is for the table only");
    }

    [Fact]
    public async Task GroupList_UnknownGroup_IsAnInputError()
    {
        var loadout = await CreateLoadout();

        var (exit, output) = await Run("loadout", "group", "list", "-l", loadout.LoadoutId.ToString(), "-g", "Nope");

        exit.Should().Be(-1, "the renderer's input error code; the verb used to throw instead");
        output.Should().Contain("Nope");
    }

    [Fact]
    public async Task Revisions_Json_NumbersMatchRevert()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout.LoadoutId, "Plain Mod");
        await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod");

        var (exit, output) = await Run("loadout", "revisions", "-l", loadout.LoadoutId.ToString(), "--json");

        exit.Should().Be(0, output);
        var rows = JsonNode.Parse(output)!.AsArray().Select(n => n!.AsObject()).ToArray();
        rows.Select(r => (int)r["number"]!).Should().Equal(Enumerable.Range(0, rows.Length));
        rows.Should().OnlyContain(r => r["timestamp"] != null && r["tx"] != null && r["modCount"] != null);
        ((int)rows[^1]["number"]!).Should().Be(LatestRevisionNumber(loadout.LoadoutId));

        var (revertExit, revertOutput) = await Run("loadout", "revert", "-l", loadout.LoadoutId.ToString(), "-r", ((int)rows[^1]["number"]!).ToString());

        revertExit.Should().Be(0, revertOutput);
        IsDisabled(mod).Should().BeFalse("the newest listed number is the restore point written by the disable");
    }
}
