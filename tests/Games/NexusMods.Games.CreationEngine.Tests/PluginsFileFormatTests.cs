using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>PluginsFile takes its data folder and its marker from the game it is built for.</summary>
public class PluginsFileFormatTests(IServiceProvider serviceProvider)
{
    private static readonly GamePath DataFiles = new(LocationId.Game, "Data Files");

    private static ICreationEngineGame GameWith()
    {
        var game = Substitute.For<ICreationEngineGame>();
        game.DataPath.Returns(DataFiles);
        game.PluginsFile.Returns(new GamePath(LocationId.AppData, "Plugins.txt"));
        game.ParsePlugin(Hash.From(1), null).ReturnsForAnyArgs(call =>
        {
            var name = call.ArgAt<RelativePath?>(1);
            var fileName = name!.Value.FileName.ToString();
            return ValueTask.FromResult<PluginHeader?>(new PluginHeader(fileName, [], 0, null, null, null, null, PluginFormat.Tes4));
        });
        return game;
    }

    private static SyncNode Node() => new()
    {
        Loadout = new SyncNodePart { Hash = Hash.From(1), Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 },
        SourceItemType = LoadoutSourceItemType.Game,
    };

    private static IPluginListCodec Mechanism(string marker) => marker == "*" ? (IPluginListCodec)new AsteriskMechanism() : new PlainFormat();

    private async Task<string> Write(string marker)
    {
        var plugins = new PluginsFile(NullLogger<PluginsFile>.Instance, GameWith(), Mechanism(marker));
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "Data Files/A.esp")] = Node(),
            [new GamePath(LocationId.Game, "Data/B.esp")] = Node(),
        };

        using var stream = new MemoryStream();
        await plugins.Render(stream, null!, [], [], tree);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<byte[]> WriteNamed(IPluginListCodec codec, string name)
    {
        var plugins = new PluginsFile(NullLogger<PluginsFile>.Instance, GameWith(), codec);
        var tree = new Dictionary<GamePath, SyncNode> { [new GamePath(LocationId.Game, $"Data Files/{name}")] = Node() };

        using var stream = new MemoryStream();
        await plugins.Render(stream, null!, [], [], tree);
        return stream.ToArray();
    }

    [Fact]
    public async Task Write_PluginsTxt_EncodesNamesAsWindows1252()
    {
        var bytes = await WriteNamed(new PlainFormat(), "\u00dcn\u00efc\u00f6d\u00e9.esp");

        bytes.Should().Contain(0xDC).And.Contain(0xE9);
        Encoding.UTF8.GetString(bytes).Should().NotContain("\u00dc");
    }

    [Fact]
    public async Task Write_LoadOrderTxt_EncodesNamesAsUtf8()
    {
        var bytes = await WriteNamed(new LoadOrderFormat(), "\u00dcn\u00efc\u00f6d\u00e9.esp");

        Encoding.UTF8.GetString(bytes).Should().Contain("\u00dcn\u00efc\u00f6d\u00e9.esp");
    }

    [Fact]
    public async Task Write_PluginsTxt_RefusesANameWindows1252CannotHold_NamingThePlugin()
    {
        var act = () => WriteNamed(new AsteriskMechanism(), "\u65e5\u672c.esp");

        (await act.Should().ThrowAsync<UnencodablePluginNamesException>()).Which.PluginNames.Should().Equal("\u65e5\u672c.esp");
    }

    [Fact]
    public void MorrowindIni_RoundTripsWindows1252NamesThatLatin1WouldLose()
    {
        IPluginListCodec codec = new MorrowindIniFormat();
        byte[] bytes = [0x80, 0x8A, (byte)'.', (byte)'e', (byte)'s', (byte)'p'];

        codec.Decode(bytes).Should().Be("\u20ac\u0160.esp");
        codec.Encoding.GetBytes("\u20ac\u0160.esp").Should().Equal(bytes);
    }

    [Fact]
    public void OpenMwConfig_RefusesBytesThatArentUtf8_NamingTheFile()
    {
        var act = () => new OpenMwConfigFormat().Decode([.. "content="u8, 0xDC, .. ".esp\n"u8]);

        act.Should().Throw<UndecodablePluginListException>().Which.FileName.Should().Be("openmw.cfg");
    }

    [Fact]
    public void OpenMwConfig_ReadsUtf8WithoutItsBom()
    {
        new OpenMwConfigFormat().Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("content=\u00dc.esp\n")])
            .Should().Be("content=\u00dc.esp\n");
    }

    [Fact]
    public async Task Write_Plain_ListsOnlyPluginsInTheGamesDataFolderWithoutAMarker()
    {
        var lines = (await Write("")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Where(l => !l.StartsWith('#')).Should().Equal("A.esp");
    }

    [Fact]
    public async Task Write_Star_MarksEachPluginActive()
    {
        var lines = (await Write("*")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Where(l => !l.StartsWith('#')).Should().Equal("*A.esp");
    }

    [Fact]
    public void SkyrimSE_UsesStarMarkersAndTheDataFolder()
    {
        var skyrim = serviceProvider.GetServices<NexusMods.Abstractions.Games.IGame>().OfType<CreationEngine.SkyrimSE.SkyrimSE>().Single();

        skyrim.PluginList(null!).Should().BeOfType<AsteriskMechanism>();
        skyrim.DataPath.Should().Be(new GamePath(LocationId.Game, "Data"));
    }
}
