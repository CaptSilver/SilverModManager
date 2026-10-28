using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Order;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Order;

/// <summary>
/// The Witcher 3's <c>loadOrder</c> key as Vortex's game extension writes it: a list of entries per Mods folder, the
/// folder as <c>id</c>, the install id as <c>modId</c>, and the mods.settings priority as <c>data.prefix</c>.
/// </summary>
public class Witcher3VortexOrderFormatTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3VortexOrderFormatTests, Witcher3Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static readonly Witcher3VortexOrderFormat Format = new();

    private CollectionRoot FixtureRoot() =>
        JsonSerializer.Deserialize<CollectionRoot>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Witcher3", "Order", "Resources", "ultimate-mod-list-shape.json")),
            ServiceProvider.GetRequiredService<JsonSerializerOptions>())!;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ModOrderSourceEntry[] Entries(IReadOnlyList<ModOrderSourceEntry> entries) => entries.ToArray();

    private static string?[] Names(IReadOnlyList<ModOrderSourceEntry> entries) => entries.Select(e => e.Reference.Name).ToArray();

    private (IReadOnlyList<ModOrderSourceEntry> Entries, IReadOnlyList<ModOrderDrop> Unmapped) ReadFixture()
    {
        var root = FixtureRoot();
        return Format.Read(root.LoadOrder!.Value, root);
    }

    [Fact]
    public void Key_IsLoadOrder() => Format.Key.Should().Be("loadOrder");

    [Fact]
    public void Read_OrdersByPrefixWinnerFirst()
    {
        var (entries, _) = ReadFixture();

        // The fixture lists the entries out of order; prefix 2 is mods.settings Priority 2, the first folder the game reads.
        Names(entries).Should().Equal("Better Icons", "Friendly HUD", "Extra Quests", "Mod Menu", "Sharper Shadows");
    }

    [Fact]
    public void Read_SkipsTheLockedMergedEntry()
    {
        var (entries, unmapped) = ReadFixture();

        entries.Select(e => e.Reference.Name).Should().NotContain("mod0000_MergedFiles");
        unmapped.Select(d => d.Item).Should().NotContain("mod0000_MergedFiles");
    }

    [Fact]
    public void Read_SkipsAMergedFolderThatIsNotMarkedLocked()
    {
        var root = FixtureRoot();

        var (entries, unmapped) = Format.Read(Json("""[{"id":"mod0000_Other","modId":"mod0000_Other","enabled":true,"data":{"prefix":1}}]"""), root);

        entries.Should().BeEmpty();
        unmapped.Should().BeEmpty();
    }

    [Fact]
    public void Read_JoinsByLogicalFilename_FileExpressionAndInstallIdNumber()
    {
        var (entries, _) = ReadFixture();

        // "Friendly HUD" joins by logicalFilename, "Mod Menu v2.1" by fileExpression, the rest by the number in the install id.
        var byName = entries.ToDictionary(e => e.Reference.Name!);
        (byName["Friendly HUD"].Reference.ModId, byName["Friendly HUD"].Reference.FileId).Should().Be((ModId.From(365), FileId.From(1234)));
        (byName["Mod Menu"].Reference.ModId, byName["Mod Menu"].Reference.FileId).Should().Be((ModId.From(3000), FileId.From(20)));
        (byName["Better Icons"].Reference.ModId, byName["Better Icons"].Reference.FileId).Should().Be((ModId.From(2000), FileId.From(10)));
        (byName["Extra Quests"].Reference.ModId, byName["Extra Quests"].Reference.FileId).Should().Be((ModId.From(4000), FileId.From(30)));
    }

    [Fact]
    public void Read_ReportsAnEntryThatMatchesNoMod()
    {
        var (_, unmapped) = ReadFixture();

        var drop = unmapped.Should().ContainSingle().Which;
        drop.Item.Should().Be("modGhost");
        drop.Reason.Should().Contain("Ghost Mod-777-1-0-1700000003");
        drop.Page.Should().Be("Load Order", "the page that holds this order, not the file-conflicts tab the default sink names");
    }

    [Fact]
    public void Read_DoesNotGuessBetweenTwoModsThatShareANexusModId()
    {
        var root = FixtureRoot();
        var twin = new Mod { Name = "Twin B", DomainName = root.Mods[0].DomainName, Source = new ModSource { Type = ModSourceType.NexusMods, ModId = ModId.From(2000), FileId = FileId.From(11) } };
        var ambiguous = new CollectionRoot { Info = root.Info, Mods = [.. root.Mods, twin], LoadOrder = root.LoadOrder };

        var (entries, unmapped) = Format.Read(ambiguous.LoadOrder!.Value, ambiguous);

        Names(entries).Should().NotContain("Better Icons");
        unmapped.Select(d => d.Item).Should().Contain("modBetterIcons");
    }

    [Fact]
    public void Read_CarriesEnabled()
    {
        var (entries, _) = ReadFixture();

        entries.Single(e => e.Reference.Name == "Mod Menu").Enabled.Should().BeFalse();
        entries.Where(e => e.Reference.Name != "Mod Menu").Should().OnlyContain(e => e.Enabled == true);
    }

    [Fact]
    public void Read_AcceptsTheLegacyObjectMap()
    {
        var root = FixtureRoot();

        var (entries, unmapped) = Format.Read(Json("""
            {"Mod Menu":{"enabled":false,"prefix":"0002"},"Friendly HUD":{"enabled":true,"prefix":"0001"},
             "mod0000_MergedFiles":{"enabled":true,"locked":true,"prefix":"0000"}}
            """), root);

        Names(entries).Should().Equal("Friendly HUD", "Mod Menu");
        entries[1].Enabled.Should().BeFalse();
        unmapped.Should().BeEmpty();
    }

    [Fact]
    public void Read_KeepsListedOrderForEqualPrefixes()
    {
        var root = FixtureRoot();

        var (entries, _) = Format.Read(Json("""
            [{"id":"b","modId":"Friendly HUD","data":{"prefix":4}},{"id":"a","modId":"Mod Menu v2.1","data":{"prefix":4}}]
            """), root);

        Names(entries).Should().Equal("Friendly HUD", "Mod Menu");
    }

    [Fact]
    public void Read_ListsAModOnceWhenItHasTwoFolders()
    {
        var root = FixtureRoot();

        var (entries, unmapped) = Format.Read(Json("""
            [{"id":"modA","modId":"Friendly HUD","data":{"prefix":2}},{"id":"modB","modId":"Friendly HUD","data":{"prefix":3}}]
            """), root);

        Names(entries).Should().Equal("Friendly HUD");
        unmapped.Should().BeEmpty();
    }

    [Fact]
    public void Read_ReportsAValueThatIsNotAList()
    {
        var (entries, unmapped) = Format.Read(Json("\"nope\""), FixtureRoot());

        entries.Should().BeEmpty();
        unmapped.Should().ContainSingle().Which.Page.Should().Be("Load Order");
    }

    // Write

    private record Mod3(string Name, uint ModId, uint FileId, string FileName, bool Disabled, params string[] Files);

    /// <summary>Nexus-linked groups under one container, each holding the given Mods files; returns the groups in the order given.</summary>
    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly[] Groups)> Build(params Mod3[] mods)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Pack");
        var groups = new List<LoadoutItemGroupId>();
        foreach (var mod in mods)
        {
            var item = ShareableMods.AddNexusItem(tx, mod.Name, Witcher3Game.NexusModsGameId.Value, GameDomain.From("witcher3"), mod.ModId, mod.FileId, mod.FileName);
            var group = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, mod.Name, item);
            foreach (var file in mod.Files) AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, file));
            if (mod.Disabled) tx.Add(group.Value, LoadoutItem.Disabled, Null.Instance);
            groups.Add(group);
        }
        var result = await tx.Commit();
        return (Refresh(loadout), groups.Select(g => LoadoutItemGroup.Load(result.Db, LoadoutItemGroupId.From(result[g.Value]))).ToArray());
    }

    private static Mod3 Hud(bool disabled = false) =>
        new("Friendly HUD", 365, 1234, "Friendly HUD-365-1234-3-2-1700000000.zip", disabled, "Mods/modFriendlyHUD/content/scripts/game/hud.ws");

    private static Mod3 Icons() =>
        new("Better Icons", 2000, 10, "Better Icons-2000-1-0-1700000001.zip", false, "Mods/modBetterIcons/content/scripts/game/icons.ws", "Mods/modBetterIcons/content/menu.xml");

    private static Mod3 Menu() =>
        new("Menu tweak", 3000, 20, "Menu tweak-3000-1-0-1700000002.zip", false, "bin/config/r4game/user_config_matrix/pc/x.xml");

    [Fact]
    public async Task Write_ListsTheLockedMergedFolderFirstThenTheFoldersWinnerFirst()
    {
        var (loadout, groups) = await Build(Hud(), Icons());

        var written = Format.Write(loadout, [groups[1], groups[0]])!.Value;

        var entries = written.EnumerateArray().ToArray();
        entries.Select(e => e.GetProperty("id").GetString()).Should().Equal("mod0000_MergedFiles", "modBetterIcons", "modFriendlyHUD");
        entries[0].GetProperty("locked").GetBoolean().Should().BeTrue();
        entries.Skip(1).Should().OnlyContain(e => !e.GetProperty("locked").GetBoolean());
        entries.Select(e => e.GetProperty("data").GetProperty("prefix").GetInt32()).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Write_EntriesHaveVortexsShape_AndIdIsTheModsFolder()
    {
        var (loadout, groups) = await Build(Hud(), Icons());

        var written = Format.Write(loadout, groups)!.Value;

        foreach (var entry in written.EnumerateArray())
        {
            entry.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("id", "name", "enabled", "modId", "locked", "data");
            entry.GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
            entry.GetProperty("name").ValueKind.Should().Be(JsonValueKind.String);
            entry.GetProperty("modId").ValueKind.Should().Be(JsonValueKind.String);
            entry.GetProperty("enabled").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
            entry.GetProperty("locked").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
            entry.GetProperty("data").EnumerateObject().Select(p => p.Name).Should().Equal("prefix");
            entry.GetProperty("data").GetProperty("prefix").ValueKind.Should().Be(JsonValueKind.Number);
        }

        // Vortex matches mods.settings sections by id, so id is the folder under Mods, not the archive or the group.
        var hud = written.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "modFriendlyHUD");
        hud.GetProperty("modId").GetString().Should().Be("Friendly HUD-365-1234-3-2-1700000000", "the install id is the archive name without its extension");
    }

    [Fact]
    public async Task Write_CarriesTheSwitch()
    {
        var (loadout, groups) = await Build(Hud(disabled: true), Icons());

        var written = Format.Write(loadout, groups)!.Value;

        var byId = written.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("enabled").GetBoolean());
        byId["modFriendlyHUD"].Should().BeFalse();
        byId["modBetterIcons"].Should().BeTrue();
    }

    [Fact]
    public async Task Write_WritesOneEntryPerFolderOfAGroup()
    {
        var (loadout, groups) = await Build(new Mod3("Two folders", 10, 1, "Two-10-1-0-1.zip", false, "Mods/modOne/a.ws", "Mods/modTwo/b.ws"));

        var written = Format.Write(loadout, groups)!.Value;

        written.EnumerateArray().Skip(1).Select(e => e.GetProperty("id").GetString()).Should().Equal("modOne", "modTwo");
    }

    [Fact]
    public async Task Write_LeavesOutAGroupWithoutAModsFolder_AndReturnsNullWhenNoneHasOne()
    {
        var (loadout, groups) = await Build(Menu(), Hud());

        var written = Format.Write(loadout, groups)!.Value;
        written.EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().Equal("mod0000_MergedFiles", "modFriendlyHUD");

        Format.Write(loadout, [groups[0]]).Should().BeNull();
        Format.Write(loadout, []).Should().BeNull();
    }

    [Fact]
    public async Task Write_ThenRead_RoundTrips()
    {
        var (loadout, groups) = await Build(Hud(disabled: true), Icons());
        var written = Format.Write(loadout, [groups[1], groups[0]])!.Value;
        var mods = new[]
        {
            new Mod { Name = "Friendly HUD", DomainName = GameDomain.From("witcher3"), Source = new ModSource { Type = ModSourceType.NexusMods, ModId = ModId.From(365), FileId = FileId.From(1234) } },
            new Mod { Name = "Better Icons", DomainName = GameDomain.From("witcher3"), Source = new ModSource { Type = ModSourceType.NexusMods, ModId = ModId.From(2000), FileId = FileId.From(10) } },
        };
        var root = new CollectionRoot { Info = new CollectionInfo { Name = "x", DomainName = GameDomain.From("witcher3") }, Mods = mods, LoadOrder = written };

        var (entries, unmapped) = Format.Read(written, root);

        Names(entries).Should().Equal("Better Icons", "Friendly HUD");
        entries.Select(e => e.Enabled).Should().Equal(true, false);
        unmapped.Should().BeEmpty();
    }
}
