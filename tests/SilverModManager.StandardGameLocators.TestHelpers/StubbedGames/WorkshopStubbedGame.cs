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
/// A stubbed game laid out as a Steam library keeps it (the game folder is steamapps/common/&lt;game&gt;), with Steam Workshop items and no other mod source.
/// </summary>
public class WorkshopStubbedGame : IGame, IGameData<WorkshopStubbedGame>, ISteamWorkshopGame
{
    public static GameId GameId { get; } = GameId.From("WorkshopStubbedGame");
    public static string DisplayName => "Workshop Stubbed Game";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [7047u],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public WorkshopStubbedGame(IServiceProvider provider)
    {
        _serviceProvider = provider;
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new DefaultSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path },
        }.ToImmutableDictionary();
    }

    public uint SteamWorkshopAppId => 268500;

    public bool HasOrderFile => false;

    public ILibraryItemInstaller[] LibraryItemInstallers =>
    [
        new StubbedGameInstaller(_serviceProvider),
    ];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);
}
