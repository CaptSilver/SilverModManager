using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.LoadOrder.Ordering;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// What ends up in the plugin list file for a given sort order: its switches, its order, the plugins the game
/// loads on its own, and the load order rules the game applies to whatever the file says.
/// </summary>
public class PluginsFileOrderingTests
{
    private static readonly GamePath DataFolder = new(LocationId.Game, "Data");

    // A fake game that reads the masters of a plugin from a table and tells masters by extension, like Skyrim.
    private static ICreationEngineGame Game(IReadOnlyDictionary<string, string[]> masters, params string[] blueprints)
    {
        var game = Substitute.For<ICreationEngineGame>();
        game.DataPath.Returns(DataFolder);
        game.PluginsFile.Returns(new GamePath(LocationId.AppData, "Plugins.txt"));
        game.ParsePlugin(Hash.From(1), null).ReturnsForAnyArgs(call =>
        {
            var name = call.ArgAt<RelativePath?>(1)!.Value.FileName.ToString();
            return ValueTask.FromResult<PluginHeader?>(new PluginHeader(name, masters.GetValueOrDefault(name, []), 0, null, null, null, null, PluginFormat.Tes4));
        });
        game.IsMaster(default!, default!).ReturnsForAnyArgs(call =>
            call.ArgAt<PluginHeader>(0).FileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase));
        game.IsBlueprint(default!).ReturnsForAnyArgs(call => blueprints.Contains(call.ArgAt<PluginHeader>(0).FileName));
        return game;
    }

    private static Dictionary<GamePath, SyncNode> Tree(params string[] names) => names.ToDictionary(
        name => new GamePath(LocationId.Game, "Data/" + name),
        _ => new SyncNode
        {
            Loadout = new SyncNodePart { Hash = Hash.From(1), Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 },
            SourceItemType = LoadoutSourceItemType.Game,
        });

    private static PluginSortData Row(string name, int index, bool enabled = true) => new(name, index, enabled);

    private static async Task<string[]> Lines(
        ICreationEngineGame game,
        IPluginListCodec mechanism,
        string[] tree,
        PluginSortData[] rows,
        params string[] earlyLoaders)
    {
        var file = new PluginsFile(NullLogger<PluginsFile>.Instance, game, mechanism);
        using var stream = new MemoryStream();
        await file.Render(stream, null!, earlyLoaders, rows, Tree(tree));
        return Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
    }

    private static readonly Dictionary<string, string[]> NoMasters = new();

    [Fact]
    public async Task ADisabledSortItem_RendersWithoutTheStar()
    {
        var lines = await Lines(Game(NoMasters), new AsteriskMechanism(), ["A.esp", "B.esp"], [Row("A.esp", 0), Row("B.esp", 1, enabled: false)]);

        lines.Should().Equal("*A.esp", "B.esp");
    }

    [Fact]
    public async Task ADisabledSortItem_IsLeftOutOfAPlainList()
    {
        var lines = await Lines(Game(NoMasters), new PlainFormat(), ["A.esp", "B.esp"], [Row("A.esp", 0), Row("B.esp", 1, enabled: false)]);

        lines.Should().Equal("A.esp");
    }

    [Fact]
    public async Task AMovedSortItem_ChangesTheLineOrder()
    {
        var game = Game(NoMasters);
        var before = await Lines(game, new AsteriskMechanism(), ["A.esp", "B.esp", "C.esp"], [Row("A.esp", 0), Row("B.esp", 1), Row("C.esp", 2)]);
        var after = await Lines(game, new AsteriskMechanism(), ["A.esp", "B.esp", "C.esp"], [Row("C.esp", 0), Row("A.esp", 1), Row("B.esp", 2)]);

        before.Should().Equal("*A.esp", "*B.esp", "*C.esp");
        after.Should().Equal("*C.esp", "*A.esp", "*B.esp");
    }

    [Fact]
    public async Task APluginWithoutARow_RendersActiveAfterThePlacedOnes()
    {
        var lines = await Lines(Game(NoMasters), new AsteriskMechanism(), ["Z.esp", "A.esp", "New.esp"], [Row("Z.esp", 0), Row("A.esp", 1)]);

        lines.Should().Equal("*Z.esp", "*A.esp", "*New.esp");
    }

    [Fact]
    public async Task EarlyLoaders_AreLeftOutOfAnAsteriskListButWrittenToAPlainOne()
    {
        var game = Game(NoMasters);
        string[] tree = ["Skyrim.esm", "A.esp"];
        PluginSortData[] rows = [Row("Skyrim.esm", 0), Row("A.esp", 1)];

        var skyrimSE = await Lines(game, new AsteriskMechanism(), tree, rows, "Skyrim.esm");
        var skyrimLE = await Lines(game, new PlainFormat(), tree, rows, "Skyrim.esm");

        skyrimSE.Should().Equal("*A.esp");
        skyrimLE.Should().Equal("Skyrim.esm", "A.esp");
    }

    [Fact]
    public async Task AnEarlyLoader_AlwaysRendersActiveAndFirst()
    {
        // The row says it is off and last, which the Load Order page never lets happen, but a stale row must not win.
        var lines = await Lines(Game(NoMasters), new PlainFormat(), ["Skyrim.esm", "A.esp"], [Row("A.esp", 0), Row("Skyrim.esm", 1, enabled: false)], "Skyrim.esm");

        lines.Should().Equal("Skyrim.esm", "A.esp");
    }

    [Fact]
    public async Task APluginWithAMissingMaster_IsStillWritten()
    {
        var game = Game(new Dictionary<string, string[]> { ["A.esp"] = ["Gone.esm"] });

        var lines = await Lines(game, new AsteriskMechanism(), ["A.esp"], [Row("A.esp", 0)]);

        lines.Should().Equal("*A.esp");
    }

    [Fact]
    public async Task AMasterWhoseMasterIsAnEsp_RendersAfterThatEsp()
    {
        // The game loads Late.esp ahead of Needs.esm anyway, so the file has to say so.
        var game = Game(new Dictionary<string, string[]> { ["Needs.esm"] = ["Late.esp"] });

        var lines = await Lines(game, new AsteriskMechanism(), ["Needs.esm", "Late.esp"], [Row("Needs.esm", 0), Row("Late.esp", 1)]);

        lines.Should().Equal("*Late.esp", "*Needs.esm");
    }

    [Fact]
    public async Task AMasterPlacedBelowAPlugin_StillComesFirst()
    {
        var lines = await Lines(Game(NoMasters), new AsteriskMechanism(), ["A.esp", "Core.esm"], [Row("A.esp", 0), Row("Core.esm", 1)]);

        lines.Should().Equal("*Core.esm", "*A.esp");
    }

    [Fact]
    public async Task ABlueprintMaster_RendersAfterTheNonMastersAndIsNotWrittenEarlyEvenWhenHardcoded()
    {
        var game = Game(NoMasters, blueprints: "Blueprint.esm");
        string[] tree = ["Blueprint.esm", "A.esp", "Core.esm"];
        PluginSortData[] rows = [Row("Blueprint.esm", 0), Row("A.esp", 1), Row("Core.esm", 2)];

        // Hardcoded, but a blueprint master never loads early, so a plain list that writes early loaders still has it last.
        var plain = await Lines(game, new PlainFormat(), tree, rows, "Blueprint.esm", "Core.esm");
        var asterisk = await Lines(game, new AsteriskMechanism(), tree, rows, "Blueprint.esm", "Core.esm");

        plain.Should().Equal("Core.esm", "A.esp", "Blueprint.esm");
        asterisk.Should().Equal("*A.esp", "*Blueprint.esm");
    }

    [Fact]
    public async Task APluginPairThatListEachOtherAsMasters_IsRefused()
    {
        var game = Game(new Dictionary<string, string[]> { ["A.esm"] = ["B.esm"], ["B.esm"] = ["A.esm"] });

        var act = () => Lines(game, new AsteriskMechanism(), ["A.esm", "B.esm"], []);

        await act.Should().ThrowAsync<PluginOrderCycleException>();
    }

    [Fact]
    public async Task WithNoPlugins_TheFileIsStillWritten()
    {
        var file = new PluginsFile(NullLogger<PluginsFile>.Instance, Game(NoMasters), new PlainFormat());
        using var stream = new MemoryStream();

        await file.Render(stream, null!, [], [], Tree());

        stream.Length.Should().BeGreaterThan(0, "an empty Plugins.txt has to be rendered, not skipped");
    }
}
