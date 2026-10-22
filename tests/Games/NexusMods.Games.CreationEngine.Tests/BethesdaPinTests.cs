using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.CreationEngine.Emitters;
using NexusMods.Games.CreationEngine.Installers;
using NexusMods.Games.FOMOD;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>
/// What Skyrim SE and Fallout 4 are made of: the order of their emitters and installers, what their
/// synchronizers leave out of backups, and which tool runs them. Resolved from the real service graph.
/// </summary>
public class BethesdaPinTests(IServiceProvider serviceProvider)
{
    private T GameOf<T>() where T : IGame => serviceProvider.GetServices<IGame>().OfType<T>().Single();

    private IGame[] Titles => [GameOf<CreationEngine.SkyrimSE.SkyrimSE>(), GameOf<CreationEngine.Fallout4.Fallout4>()];

    [Fact]
    public void EmittersAreTheMissingMasterCheckThenTheProtonPrefixCheck()
    {
        GameOf<CreationEngine.SkyrimSE.SkyrimSE>().DiagnosticEmitters.Select(e => e.GetType())
            .Should().Equal(typeof(MissingMasterEmitter), typeof(ProtonPrefixEmitter));
    }

    // Fallout 4 reads sTestFile entries from its ini, so it has one more check than Skyrim SE.
    [Fact]
    public void Fallout4AlsoChecksTheIniForTestFiles()
    {
        GameOf<CreationEngine.Fallout4.Fallout4>().DiagnosticEmitters.Select(e => e.GetType())
            .Should().Equal(typeof(MissingMasterEmitter), typeof(TestFileEntriesEmitter), typeof(ProtonPrefixEmitter));
    }

    [Fact]
    public void InstallersAreFomodAtDataThenStopPattern()
    {
        foreach (var game in Titles)
        {
            game.LibraryItemInstallers.Should().HaveCount(2);
            game.LibraryItemInstallers[0].Should().BeOfType<FomodXmlInstaller>();
            game.LibraryItemInstallers[1].Should().BeOfType<StopPatternInstaller>();
        }
    }

    [Theory]
    [InlineData("Data/Skyrim - Textures0.bsa", true)]
    [InlineData("Data/Fallout4 - Main.ba2", true)]
    [InlineData("Data/Mod.esp", false)]
    [InlineData("Data/textures/a.dds", false)]
    public void ArchivesAreNotBackedUp(string path, bool ignored)
    {
        foreach (var game in Titles)
            ((ALoadoutSynchronizer)game.Synchronizer).IsIgnoredBackupPath(new GamePath(LocationId.Game, path)).Should().Be(ignored);
    }

    [Fact]
    public void EachTitleRunsThroughTheScriptExtenderTool()
    {
        var tools = serviceProvider.GetServices<ITool>().ToArray();
        tools.OfType<RunGameViaScriptExtenderTool<CreationEngine.SkyrimSE.SkyrimSE>>().Should().ContainSingle();
        tools.OfType<RunGameViaScriptExtenderTool<CreationEngine.Fallout4.Fallout4>>().Should().ContainSingle();
    }

    [Fact]
    public void ScriptExtenderLoaderPathsKeepTheirValues()
    {
        KnownPaths.SKSE64Loader.Should().Be(new GamePath(LocationId.Game, "skse64_loader.exe"));
        KnownPaths.F4SELoader.Should().Be(new GamePath(LocationId.Game, "f4se_loader.exe"));
    }

    [Fact]
    public void PluginsFilesAndPrimaryFilesKeepTheirNames()
    {
        var skyrim = GameOf<CreationEngine.SkyrimSE.SkyrimSE>();
        var fallout = GameOf<CreationEngine.Fallout4.Fallout4>();

        // Skyrim SE capitalises Plugins.txt, Fallout 4 does not.
        skyrim.PluginsFile.Should().Be(new GamePath(LocationId.AppData, "Plugins.txt"));
        fallout.PluginsFile.Should().Be(new GamePath(LocationId.AppData, "plugins.txt"));
        skyrim.GetPrimaryFile(null!).Should().Be(new GamePath(LocationId.Game, "SkyrimSE.exe"));
        fallout.GetPrimaryFile(null!).Should().Be(new GamePath(LocationId.Game, "Fallout4.exe"));
    }

    private static IReadOnlyDictionary<LocationId, AbsolutePath> Locate(IGame game, GameStore store)
    {
        var fs = new InMemoryFileSystem();
        var result = new GameLocatorResult
        {
            Game = (IGameData)game,
            Path = fs.FromUnsanitizedFullPath("/games/install"),
            Store = store,
            StoreIdentifier = "id",
            LocatorIds = [],
            Locator = null!,
        };
        return ((IGameData)game).GetLocations(fs, result);
    }

    [Theory]
    [InlineData(typeof(CreationEngine.SkyrimSE.SkyrimSE), "Skyrim Special Edition", "Skyrim Special Edition")]
    [InlineData(typeof(CreationEngine.Fallout4.Fallout4), "Fallout4", "Fallout4")]
    public void LocationsSitUnderTheTitlesOwnFolders(Type gameType, string appData, string myGames)
    {
        var game = Titles.Single(g => g.GetType() == gameType);
        var fs = new InMemoryFileSystem();
        var locations = Locate(game, GameStore.Steam);

        locations[LocationId.Game].GetFullPath().Should().Be("/games/install");
        locations[LocationId.AppData].FileName.Should().Be(appData);
        locations[LocationId.AppData].Parent.Should().Be(fs.GetKnownPath(KnownPath.LocalApplicationDataDirectory));
        locations[LocationId.Preferences].FileName.Should().Be(myGames);
        locations[LocationId.Preferences].Parent.Should().Be(fs.GetKnownPath(KnownPath.MyGamesDirectory));
    }

    [Fact]
    public void OnlySkyrimSEGivesItsFoldersAGogPostfix()
    {
        Locate(GameOf<CreationEngine.SkyrimSE.SkyrimSE>(), GameStore.GOG)[LocationId.Preferences].FileName.Should().Be("Skyrim Special Edition GOG");
        Locate(GameOf<CreationEngine.SkyrimSE.SkyrimSE>(), GameStore.GOG)[LocationId.AppData].FileName.Should().Be("Skyrim Special Edition GOG");
        Locate(GameOf<CreationEngine.SkyrimSE.SkyrimSE>(), GameStore.Steam)[LocationId.AppData].FileName.Should().Be("Skyrim Special Edition");
        Locate(GameOf<CreationEngine.Fallout4.Fallout4>(), GameStore.GOG)[LocationId.Preferences].FileName.Should().Be("Fallout4");
    }
}
