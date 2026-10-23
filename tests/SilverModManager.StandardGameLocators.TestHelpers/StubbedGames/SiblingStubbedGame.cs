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
/// A second game that shares <see cref="StubbedGame"/>'s Nexus id, the way Skyrim VR shares Skyrim SE's.
/// </summary>
public sealed class SiblingStubbedGame(IServiceProvider provider) : IGame, IGameData<SiblingStubbedGame>
{
    private readonly StubbedGame _inner = new(provider);

    public static GameId GameId { get; } = GameId.From("SiblingStubbedGame");
    public static string DisplayName => "Sibling Stubbed Game";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => StubbedGame.NexusModsGameId;

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId);
    public IStreamFactory IconImage => _inner.IconImage;
    public IStreamFactory TileImage => _inner.TileImage;
    public GamePath GetPrimaryFile(GameInstallation installation) => _inner.GetPrimaryFile(installation);
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => _inner.GetLocations(fileSystem, gameLocatorResult);
    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ILoadoutSynchronizer Synchronizer => _inner.Synchronizer;
    public ISortOrderManager SortOrderManager => _inner.SortOrderManager;
}
