using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Unity.BepInEx;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.Games.Unity.Tests.Fixtures;

/// <summary>
/// A Unity game with only the game location, a loader pack called Test-LoaderPack and one alias for it. Its names
/// match no real game, so a test that passes with it cannot be passing because a package name was hard-coded.
/// </summary>
public class TestUnityGame : IGame, IGameData<TestUnityGame>
{
    public static GameId GameId { get; } = GameId.From("Unity.TestGame");
    public static string DisplayName => "Test Unity Game";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

    public static readonly UnityGameDefinition Definition = new(
        Executable: "TestGame.exe",
        DataFolder: "TestGame_Data",
        SteamAppId: 424243,
        ThunderstoreCommunity: "test-game",
        ThunderstoreLoaderPackage: "Test-LoaderPack",
        ThunderstoreLoaderAliases: ["Other-LoaderPack"]);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId) { SteamAppIds = [Definition.SteamAppId] };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for the test game.");

    private readonly IServiceProvider _provider;

    public BepInExLoader Loader { get; }
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }

    public TestUnityGame(IServiceProvider provider)
    {
        _provider = provider;
        Loader = new BepInExLoader(Definition);
        LibraryItemInstallers = Loader.Installers(provider);
    }

    public ILoadoutSynchronizer Synchronizer => new DefaultSynchronizer(_provider);
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_provider);

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, Definition.Executable);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, gameLocatorResult.Path.Combine("game") } }.ToImmutableDictionary();
}
