using System.Collections.Immutable;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

/// <summary>
/// A stubbed game whose mods come from Thunderstore: no Nexus Mods id, community <c>repo</c>.
/// Tests that need a Thunderstore game use this one.
/// </summary>
public class ThunderstoreStubbedGame : IGame, IGameData<ThunderstoreStubbedGame>, IThunderstoreGame
{
    public static GameId GameId { get; } = GameId.From("ThunderstoreStubbedGame");
    public static string DisplayName => "Thunderstore Stubbed Game";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [44u],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public ThunderstoreStubbedGame(IServiceProvider provider)
    {
        _serviceProvider = provider;
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new DefaultSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
        }.ToImmutableDictionary();
    }

    public string ThunderstoreCommunity => "repo";
    public string ThunderstoreLoaderPackage => "BepInEx-BepInExPack";
    public IReadOnlyList<string> ThunderstoreLoaderAliases => ["bbepis-BepInExPack", "xiaoxiao921-BepInExPack"];

    public ILibraryItemInstaller[] LibraryItemInstallers =>
    [
        new StubbedGameInstaller(_serviceProvider),
    ];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);
}
