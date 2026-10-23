using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutagen.Bethesda;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>Fallout 4 VR, Fallout 76 and Starfield: folders in the prefix, plugin list handling, and what they share with their siblings.</summary>
public class FalloutStarfieldTests(IServiceProvider serviceProvider)
{
    private IGame Registered(Type type) => serviceProvider.GetServices<IGame>().Single(g => g.GetType() == type);

    [Theory]
    [InlineData(typeof(CreationEngine.Fallout4VR.Fallout4VR), "Fallout4VR", "Fallout4VR")]
    [InlineData(typeof(CreationEngine.Starfield.Starfield), "Starfield", "Starfield")]
    public void ResolvesIntoThePrefix(Type gameType, string appData, string myGames)
    {
        var prefix = ProtonTestPrefix.Create();

        var locations = Registered(gameType).GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[LocationId.AppData].Should().Be(prefix.Home.Combine($"AppData/Local/{appData}"));
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine($"Documents/My Games/{myGames}"));
    }

    [Fact]
    public void Fallout76_HasPreferencesButNoAppDataFolder()
    {
        var prefix = ProtonTestPrefix.Create();

        var locations = Registered(typeof(CreationEngine.Fallout76.Fallout76)).GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations.Should().NotContainKey(LocationId.AppData);
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine("Documents/My Games/Fallout 76"));
    }

    [Theory]
    [InlineData(typeof(CreationEngine.Fallout4VR.Fallout4VR), "Fallout4VR.exe")]
    [InlineData(typeof(CreationEngine.Fallout76.Fallout76), "Fallout76.exe")]
    [InlineData(typeof(CreationEngine.Starfield.Starfield), "Starfield.exe")]
    public void PrimaryFileIsTheGameExecutable(Type gameType, string exe) =>
        Registered(gameType).GetPrimaryFile(default!).Should().Be(new GamePath(LocationId.Game, exe));

    [Fact]
    public void Fallout4VR_WritesAStarListAtItsOwnPath()
    {
        var game = (ICreationEngineGame)Registered(typeof(CreationEngine.Fallout4VR.Fallout4VR));

        game.PluginsFile.Should().Be(new GamePath(LocationId.AppData, "plugins.txt"));
        game.PluginList(null!).Should().BeOfType<AsteriskMechanism>();
        game.DataPath.Should().Be(new GamePath(LocationId.Game, "Data"));
        game.PluginList(null!).IntrinsicFiles(game, default, serviceProvider).Select(f => f.Path).Should().Equal(game.PluginsFile);
        game.DiagnosticEmitters.Select(e => e.GetType()).Should().Contain(typeof(Emitters.MissingMasterEmitter));
    }

    [Fact]
    public async Task Fallout4VR_ReadsPluginHeaders()
    {
        var bytes = BethesdaPluginBytes.Bytes(GameRelease.Fallout4VR, "A.esp", "Fallout4.esm");
        var stored = bytes.xxHash3();
        var source = Substitute.For<IStreamSourceDispatcher>();
        source.OpenAsync(stored).Returns(_ => ValueTask.FromResult<Stream?>(new MemoryStream(bytes)));
        var game = (ICreationEngineGame)System.Activator.CreateInstance(typeof(CreationEngine.Fallout4VR.Fallout4VR), new WithStreamSource(serviceProvider, source))!;

        var header = await game.ParsePlugin(stored, RelativePath.FromUnsanitizedInput("A.esp"));

        header!.FileName.Should().Be("A.esp");
        header.Masters.Should().Equal("Fallout4.esm");
        header.Format.Should().Be(PluginFormat.Tes4);
    }

    [Fact]
    public async Task Fallout4VR_WriteListsAPluginWithTheStarMarker()
    {
        var bytes = BethesdaPluginBytes.Bytes(GameRelease.Fallout4VR, "A.esp", "Fallout4.esm");
        var stored = bytes.xxHash3();
        var source = Substitute.For<IStreamSourceDispatcher>();
        source.OpenAsync(stored).Returns(_ => ValueTask.FromResult<Stream?>(new MemoryStream(bytes)));
        var game = (ICreationEngineGame)System.Activator.CreateInstance(typeof(CreationEngine.Fallout4VR.Fallout4VR), new WithStreamSource(serviceProvider, source))!;
        var plugins = new PluginsFile(NullLogger<PluginsFile>.Instance, game, new AsteriskMechanism());
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "Data/A.esp")] = new()
            {
                Loadout = new SyncNodePart { Hash = stored, Size = NexusMods.Paths.Size.From((ulong)bytes.Length), LastModifiedTicks = 0 },
                SourceItemType = LoadoutSourceItemType.Game,
            },
        };

        using var stream = new MemoryStream();
        await plugins.Render(stream, null!, [], [], tree);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Where(l => !l.StartsWith('#')).Should().Equal("*A.esp");
    }

    // Fallout 76 has no plugin reader, so it writes no plugin list and checks no masters.
    [Fact]
    public void Fallout76_OffersNoIntrinsicFilesAndChecksNoMasters()
    {
        var game = Registered(typeof(CreationEngine.Fallout76.Fallout76));

        game.Should().NotBeAssignableTo<ICreationEngineGame>();
        ((ALoadoutSynchronizer)game.Synchronizer).IntrinsicFiles(default).Should().BeEmpty();
        game.DiagnosticEmitters.Select(e => e.GetType()).Should().NotContain(typeof(Emitters.MissingMasterEmitter));
    }

    [Fact]
    public void Starfield_WritesAStarListAtItsOwnPathAndChecksMasters()
    {
        var game = (ICreationEngineGame)Registered(typeof(CreationEngine.Starfield.Starfield));

        game.PluginsFile.Should().Be(new GamePath(LocationId.AppData, "Plugins.txt"));
        game.PluginList(null!).Should().BeOfType<AsteriskMechanism>();
        game.PluginList(null!).IntrinsicFiles(game, default, serviceProvider).Select(f => f.Path).Should().Equal(game.PluginsFile);
        game.DiagnosticEmitters.Select(e => e.GetType()).Should().Contain(typeof(Emitters.MissingMasterEmitter));
    }

    [Fact]
    public void Fallout76_IsLaunchedDirectly() =>
        serviceProvider.GetServices<ITool>().OfType<RunGameTool<CreationEngine.Fallout76.Fallout76>>().Should().ContainSingle()
            .Which.Should().NotBeAssignableTo<RunGameViaScriptExtenderTool<CreationEngine.Fallout76.Fallout76>>();

    [Fact]
    public void Starfield_IsLaunchedThroughTheScriptExtender() =>
        serviceProvider.GetServices<ITool>().OfType<RunGameViaScriptExtenderTool<CreationEngine.Starfield.Starfield>>().Should().ContainSingle();

    [Fact]
    public void Fallout4VR_IsLaunchedThroughTheScriptExtender() =>
        serviceProvider.GetServices<ITool>().OfType<RunGameViaScriptExtenderTool<CreationEngine.Fallout4VR.Fallout4VR>>().Should().ContainSingle();

    [Fact]
    public void Fallout4AndFallout4VRShareANexusIdButNotAStoreId()
    {
        var f4 = Registered(typeof(CreationEngine.Fallout4.Fallout4));
        var f4vr = Registered(typeof(CreationEngine.Fallout4VR.Fallout4VR));

        ((IGameData)f4vr).NexusModsGameId.Should().Be(((IGameData)f4).NexusModsGameId);
        ((IGameData)f4vr).StoreIdentifiers.SteamAppIds.Should().NotIntersectWith(((IGameData)f4).StoreIdentifiers.SteamAppIds);
    }

    private sealed class WithStreamSource(IServiceProvider inner, IStreamSourceDispatcher source) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IStreamSourceDispatcher) ? source : inner.GetService(serviceType);
    }
}
