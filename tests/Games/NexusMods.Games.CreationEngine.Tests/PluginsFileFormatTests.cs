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

    private static APluginsTxtMechanism Mechanism(string marker) => marker == "*" ? new AsteriskMechanism() : new PlainMechanism();

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
