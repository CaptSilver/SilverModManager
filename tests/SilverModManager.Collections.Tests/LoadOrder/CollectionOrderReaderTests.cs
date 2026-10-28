using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

public class CollectionOrderReaderTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionOrderReaderTests>(helper)
{
    private sealed class FakeFormat(params string[] winnerToLoser) : IVortexOrderFormat
    {
        public string Key => "loadOrder";

        public (IReadOnlyList<ModOrderSourceEntry> Entries, IReadOnlyList<ModOrderDrop> Unmapped) Read(JsonElement value, CollectionRoot root) =>
            (winnerToLoser.Select(name => new ModOrderSourceEntry(new ModOrderReference(Name: name), null)).ToArray(),
                [new ModOrderDrop("stray key", "not in the collection")]);

        public JsonElement? Write(Loadout.ReadOnly loadout, IReadOnlyList<LoadoutItemGroup.ReadOnly> winnerToLoser) => throw new NotSupportedException();
    }

    private sealed class VortexGame(IServiceProvider provider, IVortexOrderFormat format) : StubbedGame(provider), IModOrderGame
    {
        public IModOrderSink? OrderSink => null;
        public IVortexOrderFormat? VortexOrderFormat => format;
    }

    private static SilverLoadOrderEntry Entry(string name) => new() { Name = name, Enabled = true };

    private static CollectionRoot Root(SilverLoadOrderEntry[]? ours = null, JsonElement? vortex = null) => new()
    {
        Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("cyberpunk2077") },
        SilverLoadOrder = ours,
        LoadOrder = vortex,
    };

    private async Task<LoadoutItemGroupId> SeedContainer(params string[] names)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Collection");
        foreach (var name in names) AddEmptyGroup(tx, loadout.LoadoutId, name, container);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[container].Value);
    }

    private string[] Names(ModOrder order) =>
        order.WinnerToLoser.Select(entry => LoadoutItem.Load(Connection.Db, entry.Group.Value).Name).ToArray();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task OurKeyOrdersTheInstalledGroups()
    {
        var container = await SeedContainer("a", "b", "c");

        var order = CollectionOrderReader.Read(Connection.Db, GameInstallation.GetGame(), Root([Entry("c"), Entry("a"), Entry("b")]), container);

        Names(order).Should().Equal("c", "a", "b");
        order.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task OurKeyWinsOverAVortexKeyWithNoDropForTheVortexKey()
    {
        var container = await SeedContainer("a", "b");
        var game = new VortexGame(ServiceProvider, new FakeFormat("b", "a"));

        var order = CollectionOrderReader.Read(Connection.Db, game, Root([Entry("a"), Entry("b")], Json("{}")), container);

        Names(order).Should().Equal("a", "b");
        order.Dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task TheVortexKeyIsUsedWhenOursIsAbsent()
    {
        var container = await SeedContainer("a", "b");
        var game = new VortexGame(ServiceProvider, new FakeFormat("b", "a"));

        var order = CollectionOrderReader.Read(Connection.Db, game, Root(vortex: Json("{}")), container);

        Names(order).Should().Equal("b", "a");
        order.Dropped.Should().ContainSingle().Which.Item.Should().Be("stray key");
    }

    [Fact]
    public async Task TheVortexKeyIsIgnoredWhenTheManifestDoesNotCarryIt()
    {
        var container = await SeedContainer("a");
        var game = new VortexGame(ServiceProvider, new FakeFormat("a"));

        CollectionOrderReader.Read(Connection.Db, game, Root(), container).Should().Be(ModOrder.Empty);
    }

    [Fact]
    public async Task NoKeysNoOrder()
    {
        var container = await SeedContainer("a");

        var order = CollectionOrderReader.Read(Connection.Db, GameInstallation.GetGame(), Root(), container);

        order.WinnerToLoser.Should().BeEmpty();
        order.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void UnknownAndRawKeysRoundTripThroughTheJsonOptions()
    {
        var options = ServiceProvider.GetRequiredService<JsonSerializerOptions>();
        const string json = """
            {"info":{"name":"X","domainName":"witcher3"},"mods":[],"modRules":[],
             "loadOrder":{"a":{"pos":0}},"plugins":[{"name":"p.esp","enabled":true}],
             "silverLoadOrder":[{"md5":"00000000000000000000000000000001","modId":5,"fileId":6,"name":"n","enabled":false}]}
            """;

        var root = JsonSerializer.Deserialize<CollectionRoot>(json, options)!;
        root.SilverLoadOrder.Should().ContainSingle().Which.Should().Match<SilverLoadOrderEntry>(e => e.Name == "n" && !e.Enabled && e.ModId!.Value.Value == 5 && e.FileId!.Value.Value == 6);

        var written = JsonDocument.Parse(JsonSerializer.Serialize(root, options)).RootElement;
        written.GetProperty("loadOrder").GetProperty("a").GetProperty("pos").GetInt32().Should().Be(0);
        written.GetProperty("plugins")[0].GetProperty("name").GetString().Should().Be("p.esp");
        written.GetProperty("silverLoadOrder")[0].GetProperty("name").GetString().Should().Be("n");

        var bare = JsonDocument.Parse(JsonSerializer.Serialize(Root(), options)).RootElement;
        bare.TryGetProperty("loadOrder", out _).Should().BeFalse();
        bare.TryGetProperty("plugins", out _).Should().BeFalse();
        bare.TryGetProperty("silverLoadOrder", out _).Should().BeFalse();
    }

    private static CollectionRoot FullRoot() => new()
    {
        Info = new CollectionInfo { Name = "Test", DomainName = GameDomain.From("cyberpunk2077") },
        // Placeholder arrays: only their identity matters, and a fresh array is never the shared empty one.
        Mods = new Mod[1],
        ModRules = new ModRule[1],
        LoadOrder = Json("""{"a":1}"""),
        Plugins = Json("""[{"name":"p.esp"}]"""),
        PluginRules = Json("""{"plugins":[],"groups":[]}"""),
        MergedData = Json("""{"scriptMergedData":"00"}"""),
        SilverLoadOrder = [Entry("a")],
    };

    [Fact]
    public void FullRootSetsEveryPropertyToANonDefaultValue()
    {
        var root = FullRoot();
        foreach (var property in typeof(CollectionRoot).GetProperties())
        {
            var value = property.GetValue(root);
            var isDefault = value switch
            {
                null => true,
                JsonElement element => element.ValueKind == JsonValueKind.Undefined,
                Array array => array.Length == 0,
                _ => false,
            };
            isDefault.Should().BeFalse($"{property.Name} must be filled in FullRoot so the copy test covers it");
        }
    }

    [Theory]
    [InlineData("loadOrder")]
    [InlineData("plugins")]
    public void WithCopiesEveryPropertyOfTheRoot(string key)
    {
        var source = FullRoot();
        var replacement = Json("""{"replaced":true}""");

        var copy = VortexOrderKeys.With(source, key, replacement);

        foreach (var property in typeof(CollectionRoot).GetProperties())
        {
            var expected = property.Name == (key == "loadOrder" ? "LoadOrder" : "Plugins") ? (object)replacement : property.GetValue(source);
            var actual = property.GetValue(copy);
            if (expected is JsonElement e) ((JsonElement)actual!).GetRawText().Should().Be(e.GetRawText(), property.Name);
            else actual.Should().BeSameAs(expected, property.Name);
        }
    }

    [Fact]
    public async Task AnEmptyOursStillWinsOverAVortexKey()
    {
        var container = await SeedContainer("a", "b");
        var game = new VortexGame(ServiceProvider, new FakeFormat("b", "a"));

        var order = CollectionOrderReader.Read(Connection.Db, game, Root([], Json("{}")), container);

        order.WinnerToLoser.Should().BeEmpty();
        order.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void VortexKeysMapToTheirPropertiesInOnePlace()
    {
        var value = Json("""{"x":1}""");
        var root = VortexOrderKeys.With(Root(), "loadOrder", value);

        VortexOrderKeys.Get(root, "loadOrder")!.Value.GetRawText().Should().Be(value.GetRawText());
        VortexOrderKeys.Get(root, "plugins").Should().BeNull();
        VortexOrderKeys.With(root, "loadOrder", null).LoadOrder.Should().BeNull();
        root.Info.Name.Should().Be("Test");

        var get = () => VortexOrderKeys.Get(root, "nope");
        get.Should().Throw<ArgumentException>().WithMessage("*nope*");
        var set = () => VortexOrderKeys.With(root, "nope", value);
        set.Should().Throw<ArgumentException>().WithMessage("*nope*");
    }
}
