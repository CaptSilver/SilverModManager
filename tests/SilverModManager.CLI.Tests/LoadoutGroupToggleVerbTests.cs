using System.Text.Json.Nodes;
using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>loadout group enable</c> and <c>loadout group disable</c> switch a group through
/// <see cref="Abstractions.Loadouts.Synchronizers.ILoadoutManager.SetGroupsEnabled"/>, so every switch is a restore point.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutGroupToggleVerbTests(ITestOutputHelper helper) : EldenRingVerbTest<LoadoutGroupToggleVerbTests>(helper)
{
    [Fact]
    public async Task Disable_SetsTheMarker()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout.LoadoutId, "Plain Mod");

        var (exit, output) = await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod");

        exit.Should().Be(0, output);
        IsDisabled(mod).Should().BeTrue();
    }

    [Fact]
    public async Task Enable_RetractsTheMarker()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout.LoadoutId, "Plain Mod");
        await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod");

        var (exit, output) = await Run("loadout", "group", "enable", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod");

        exit.Should().Be(0, output);
        IsDisabled(mod).Should().BeFalse();
    }

    [Fact]
    public async Task LockedGroup_IsSkippedAndNamed()
    {
        var loadout = await CreateLoadout();
        var required = await AddMod(loadout.LoadoutId, "Required Mod");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(required.Value, NexusCollectionItemLoadoutGroup.IsRequired, true);
            await tx.Commit();
        }

        var (exit, output) = await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Required Mod");

        exit.Should().Be(1, "nothing changed");
        output.Should().Contain("Required Mod").And.Contain("required item of a collection");
        IsDisabled(required).Should().BeFalse();
    }

    [Fact]
    public async Task GroupInsideDisabledCollection_IsSkipped()
    {
        var loadout = await CreateLoadout();
        var collection = await AddCollection(loadout.LoadoutId, "My Collection");
        var inside = await AddMod(loadout.LoadoutId, "Inner Mod", collection);
        await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "My Collection");
        IsDisabled(collection).Should().BeTrue();

        var (exit, output) = await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Inner Mod");

        exit.Should().Be(1);
        output.Should().Contain("My Collection is switched off");
        IsDisabled(inside).Should().BeFalse();
    }

    [Fact]
    public async Task DuplicateName_IsRefusedWithBothIds_AndTheIdIsAccepted()
    {
        var loadout = await CreateLoadout();
        var first = await AddMod(loadout.LoadoutId, "Twin");
        var second = await AddMod(loadout.LoadoutId, "Twin");

        var (exit, output) = await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Twin");

        exit.Should().Be(1);
        output.Should().Contain(first.Value.ToString()).And.Contain(second.Value.ToString());
        IsDisabled(first).Should().BeFalse();
        IsDisabled(second).Should().BeFalse();

        var (exit2, output2) = await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", second.Value.ToString());

        exit2.Should().Be(0, output2);
        IsDisabled(second).Should().BeTrue();
        IsDisabled(first).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownGroup_IsRefused()
    {
        var loadout = await CreateLoadout();

        var (exit, output) = await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Nope");

        exit.Should().Be(1);
        output.Should().Contain("Nope");
    }

    [Fact]
    public async Task Disable_ThenRevert_RestoresTheSwitch()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout.LoadoutId, "Plain Mod");
        var revisionsBefore = LatestRevisionNumber(loadout.LoadoutId);
        await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod");
        IsDisabled(mod).Should().BeTrue();
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(revisionsBefore + 1, "the verb writes one restore point");

        var (exit, output) = await Run("loadout", "revert", "-l", loadout.LoadoutId.ToString(), "-r", LatestRevisionNumber(loadout.LoadoutId).ToString());

        exit.Should().Be(0, output);
        LoadoutItem.Load(Connection.Db, LoadoutItemId.From(mod.Value)).IsValid().Should().BeTrue("the restore point was taken with the mod present");
        IsDisabled(mod).Should().BeFalse();
    }

    [Fact]
    public async Task Enable_ThenRevert_RestoresTheSwitch()
    {
        var loadout = await CreateLoadout();
        var mod = await AddMod(loadout.LoadoutId, "Plain Mod");
        await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod");
        var revisionsBefore = LatestRevisionNumber(loadout.LoadoutId);
        await Run("loadout", "group", "enable", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod");
        IsDisabled(mod).Should().BeFalse();
        LatestRevisionNumber(loadout.LoadoutId).Should().Be(revisionsBefore + 1, "the verb writes one restore point");

        var (exit, output) = await Run("loadout", "revert", "-l", loadout.LoadoutId.ToString(), "-r", LatestRevisionNumber(loadout.LoadoutId).ToString());

        exit.Should().Be(0, output);
        LoadoutItem.Load(Connection.Db, LoadoutItemId.From(mod.Value)).IsValid().Should().BeTrue();
        IsDisabled(mod).Should().BeTrue();
    }

    [Fact]
    public async Task Json_ListsGroupsWithEnabledState()
    {
        var loadout = await CreateLoadout();
        var on = await AddMod(loadout.LoadoutId, "Switched On");
        var off = await AddMod(loadout.LoadoutId, "Switched Off");
        await Run("loadout", "group", "disable", "-l", loadout.LoadoutId.ToString(), "-g", "Switched Off");

        var (exit, output) = await Run("loadout", "groups", "list", "-l", loadout.LoadoutId.ToString(), "--json");

        exit.Should().Be(0, output);
        var rows = JsonNode.Parse(output)!.AsArray().Select(n => n!.AsObject()).ToArray();
        var onRow = rows.Single(r => (string?)r["name"] == "Switched On");
        var offRow = rows.Single(r => (string?)r["name"] == "Switched Off");
        ((bool)onRow["isEnabled"]!).Should().BeTrue();
        ((bool)offRow["isEnabled"]!).Should().BeFalse();
        ((string?)onRow["id"]).Should().Be(on.Value.ToString());
        ((int)onRow["items"]!).Should().Be(1);
    }
}
