using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

/// <summary>
/// A stubbed game with no Nexus Mods id, like a game that only exists in a store or a user definition.
/// Everything that reads a game's Nexus id has to cope with this one.
/// </summary>
public class NoNexusIdStubbedGame : IGame, IGameData<NoNexusIdStubbedGame>
{
    public static GameId GameId { get; } = GameId.From("NoNexusIdStubbedGame");
    public static string DisplayName => "Stubbed Game Without Nexus Id";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [43u],
        GOGProductIds = [43L],
        EGSCatalogItemId = ["epic-game-id-without-nexus-id"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public NoNexusIdStubbedGame(IServiceProvider provider)
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
            { LocationId.Preferences, gameLocatorResult.Path.Combine("preferences") },
            { LocationId.Saves, gameLocatorResult.Path.Combine("saves") },
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);
}
