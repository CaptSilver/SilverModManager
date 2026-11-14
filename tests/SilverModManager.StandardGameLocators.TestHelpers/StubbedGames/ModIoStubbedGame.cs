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
/// A stubbed game mod.io hosts mods for, where the app downloads the file itself, and no other mod source. Tests of the modio verbs use this one.
/// </summary>
public class ModIoStubbedGame : IGame, IGameData<ModIoStubbedGame>, IModIoGame
{
    public static GameId GameId { get; } = GameId.From("ModIoStubbedGame");
    public static string DisplayName => "mod.io Stubbed Game";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Optional<Sdk.NexusModsApi.NexusModsGameId>.None;

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [7047u],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public ModIoStubbedGame(IServiceProvider provider)
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

    public uint ModIoGameId => 4242;

    public string ModIoNameId => "stubbed";

    public bool GameSyncsSubscriptions => false;

    public ILibraryItemInstaller[] LibraryItemInstallers =>
    [
        new StubbedGameInstaller(_serviceProvider),
    ];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);
}
