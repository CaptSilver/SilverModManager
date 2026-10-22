using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>Oblivion, Fallout 3 and New Vegas: folders in the prefix, plugins ordered by file time, launch through the script extender.</summary>
public class GamebryoFalloutTests(IServiceProvider serviceProvider)
{
    private IGame Registered(Type type) => serviceProvider.GetServices<IGame>().Single(g => g.GetType() == type);

    [Theory]
    [InlineData(typeof(CreationEngine.Oblivion.Oblivion), "Oblivion", "Oblivion")]
    [InlineData(typeof(CreationEngine.Fallout3.Fallout3), "Fallout3", "Fallout3")]
    [InlineData(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), "FalloutNV", "FalloutNV")]
    public void ResolvesIntoThePrefix(Type gameType, string appData, string myGames)
    {
        var prefix = ProtonTestPrefix.Create();

        var locations = Registered(gameType).GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[LocationId.AppData].Should().Be(prefix.Home.Combine($"AppData/Local/{appData}"));
        locations[LocationId.Preferences].Should().Be(prefix.Home.Combine($"Documents/My Games/{myGames}"));
    }

    [Theory]
    [InlineData(typeof(CreationEngine.Oblivion.Oblivion), "Oblivion.exe")]
    [InlineData(typeof(CreationEngine.Fallout3.Fallout3), "Fallout3.exe")]
    [InlineData(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), "FalloutNV.exe")]
    public void PrimaryFileIsTheGameExecutable(Type gameType, string exe) =>
        Registered(gameType).GetPrimaryFile(default!).Should().Be(new GamePath(LocationId.Game, exe));

    // They keep their load order in the file times, with a plugins.txt that names the active plugins, and check masters.
    [Theory]
    [InlineData(typeof(CreationEngine.Oblivion.Oblivion), PluginFormat.Tes4Oblivion)]
    [InlineData(typeof(CreationEngine.Fallout3.Fallout3), PluginFormat.Tes4)]
    [InlineData(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas), PluginFormat.Tes4)]
    public void OrderPluginsByTimestampAndCheckMasters(Type type, PluginFormat format)
    {
        var game = Registered(type);

        game.Should().BeAssignableTo<ICreationEngineGame>().Which.HeaderFormat.Should().Be(format);
        game.Should().BeAssignableTo<ITimestampOrderedGame>();
        var mechanism = ((ICreationEngineGame)game).PluginList(null!);
        mechanism.Should().BeOfType<TimestampMechanism>().Which.OrdersByTimestamp.Should().BeTrue();
        game.DiagnosticEmitters.Select(e => e.GetType()).Should().Contain(typeof(Emitters.MissingMasterEmitter));
    }

    [Fact]
    public void Oblivion_IsLaunchedThroughTheScriptExtender() =>
        serviceProvider.GetServices<ITool>().OfType<RunGameViaScriptExtenderTool<CreationEngine.Oblivion.Oblivion>>().Should().ContainSingle();

    [Fact]
    public void Fallout3_IsLaunchedThroughTheScriptExtender() =>
        serviceProvider.GetServices<ITool>().OfType<RunGameViaScriptExtenderTool<CreationEngine.Fallout3.Fallout3>>().Should().ContainSingle();

    [Fact]
    public void FalloutNewVegas_IsLaunchedThroughTheScriptExtender() =>
        serviceProvider.GetServices<ITool>().OfType<RunGameViaScriptExtenderTool<CreationEngine.FalloutNewVegas.FalloutNewVegas>>().Should().ContainSingle();

    [Fact]
    public void StoreIdsAreTheOnesTheTitlesShipUnder()
    {
        var oblivion = ((IGameData)Registered(typeof(CreationEngine.Oblivion.Oblivion))).StoreIdentifiers;
        var fo3 = ((IGameData)Registered(typeof(CreationEngine.Fallout3.Fallout3))).StoreIdentifiers;
        var fnv = ((IGameData)Registered(typeof(CreationEngine.FalloutNewVegas.FalloutNewVegas))).StoreIdentifiers;

        oblivion.SteamAppIds.Should().BeEquivalentTo([22330u, 900883u]);
        fo3.SteamAppIds.Should().BeEquivalentTo([22300u, 22370u]);
        fnv.SteamAppIds.Should().BeEquivalentTo([22380u, 22490u]);
        new[] { oblivion, fo3, fnv }.Select(s => s.GOGProductIds.Single()).Should().Equal(1458058109L, 1454315831L, 1454587428L);
    }
}
