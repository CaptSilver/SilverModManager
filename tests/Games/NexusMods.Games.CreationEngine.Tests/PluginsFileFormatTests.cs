using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Abstractions;
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

    private static ICreationEngineGame GameWith(PluginListFormat format)
    {
        var game = Substitute.For<ICreationEngineGame>();
        game.DataPath.Returns(DataFiles);
        game.PluginListFormat.Returns(format);
        game.PluginsFile.Returns(new GamePath(LocationId.AppData, "Plugins.txt"));
        game.ParsePlugin(Hash.From(1), null).ReturnsForAnyArgs(call =>
        {
            var name = call.ArgAt<RelativePath?>(1);
            return ValueTask.FromResult<IMod?>(new SkyrimMod(ModKey.FromFileName(name!.Value.FileName.ToString()), SkyrimRelease.SkyrimSE));
        });
        return game;
    }

    private static SyncNode Node() => new()
    {
        Loadout = new SyncNodePart { Hash = Hash.From(1), Size = NexusMods.Paths.Size.From(10), LastModifiedTicks = 0 },
        SourceItemType = LoadoutSourceItemType.Game,
    };

    private async Task<string> Write(PluginListFormat format)
    {
        var plugins = new PluginsFile(NullLogger<PluginsFile>.Instance, GameWith(format), serviceProvider.GetRequiredService<ISorter>());
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "Data Files/A.esp")] = Node(),
            [new GamePath(LocationId.Game, "Data/B.esp")] = Node(),
        };

        using var stream = new MemoryStream();
        await plugins.Write(stream, default, tree);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Write_Plain_ListsOnlyPluginsInTheGamesDataFolderWithoutAMarker()
    {
        var lines = (await Write(PluginListFormat.Plain)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Where(l => !l.StartsWith('#')).Should().Equal("A.esp");
    }

    [Fact]
    public async Task Write_Star_MarksEachPluginActive()
    {
        var lines = (await Write(PluginListFormat.Star)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Where(l => !l.StartsWith('#')).Should().Equal("*A.esp");
    }

    [Fact]
    public void SkyrimSE_UsesStarMarkersAndTheDataFolder()
    {
        var skyrim = serviceProvider.GetServices<NexusMods.Abstractions.Games.IGame>().OfType<CreationEngine.SkyrimSE.SkyrimSE>().Single();

        skyrim.PluginListFormat.Should().Be(PluginListFormat.Star);
        skyrim.DataPath.Should().Be(new GamePath(LocationId.Game, "Data"));
    }
}
