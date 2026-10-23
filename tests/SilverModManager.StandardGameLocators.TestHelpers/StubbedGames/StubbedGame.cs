using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

// ReSharper disable InconsistentNaming

namespace SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

public class StubbedGame : IGame, IGameData<StubbedGame>
{
    public static GameId GameId { get; } = GameId.From("StubbedGame");
    public static string DisplayName => "Stubbed Game";

    // TODO: make None after moving to GameId
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [42u],
        GOGProductIds = [42L],
        EGSCatalogItemId = ["epic-game-id"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public StubbedGame(IServiceProvider provider)
    {
        _serviceProvider = provider;
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new DefaultSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game")},
            { LocationId.Preferences, gameLocatorResult.Path.Combine("preferences")},
            { LocationId.Saves, gameLocatorResult.Path.Combine("saves")},
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers =>
    [
        new StubbedGameInstaller(_serviceProvider),
    ];

    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    /// <summary>
    /// Create a new stubbed game installation, in its own temporary folder.
    /// </summary>
    public static async Task<GameInstallation> Create(IServiceProvider provider)
    {
        var tmpFileManager = provider.GetRequiredService<TemporaryFileManager>();
        var game = provider.GetRequiredService<StubbedGame>();
        var path = tmpFileManager.CreateFolder();

        // A native Linux install: the stub has no Wine prefix, and a Windows record without one would fail prefix resolution.
        provider.GetRequiredService<IUserGameInstallStore>().Add(new UserGameInstall(GameId, path.ToString(), OSPlatform.Linux));

        await AddTestFiles(path, provider);

        var gameRegistry = provider.GetRequiredService<IGameRegistry>();
        gameRegistry.ClearCache();
        var install = gameRegistry.LocateGameInstallations().First(g => g.Game is StubbedGame && g.LocatorResult.Path == path);

        return install;
    }

    private static async Task AddTestFiles(TemporaryPath path, IServiceProvider provider)
    {
        var stateFolder = FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine("Resources/StubbedGameState.zip");
        var extractor = provider.GetRequiredService<IFileExtractor>();
        
        await extractor.ExtractAllAsync(stateFolder, path, CancellationToken.None);

    }
}
