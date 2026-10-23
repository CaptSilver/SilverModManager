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
/// A game whose own id and Nexus id are chosen per instance, for tests that need several games, some sharing
/// a Nexus id or having none. It does nothing but answer for its ids; it is not registered anywhere.
/// </summary>
public sealed class IdentifiedStubbedGame(GameId gameId, Optional<Sdk.NexusModsApi.NexusModsGameId> nexusModsGameId) : IGame
{
    public GameId GameId { get; } = gameId;
    public string DisplayName => GameId.ToString();
    public Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; } = nexusModsGameId;

    public StoreIdentifiers StoreIdentifiers { get; } = new(gameId);

    public IStreamFactory IconImage => throw new NotSupportedException("No images for an identified stub.");
    public IStreamFactory TileImage => throw new NotSupportedException("No images for an identified stub.");

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => throw new NotSupportedException("An identified stub has no locations.");

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ILoadoutSynchronizer Synchronizer => throw new NotSupportedException("An identified stub has no synchronizer.");
    public ISortOrderManager SortOrderManager => throw new NotSupportedException("An identified stub has no sort order manager.");
}
