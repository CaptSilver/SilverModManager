using System.Collections.Immutable;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// A game to substitute with NSubstitute. <see cref="GameId"/> cannot be configured on a substitute of
/// <see cref="IGame"/>: NSubstitute builds a default value for the property first, and a game id has no
/// default. Here it is a plain property; the rest is abstract so a substitute can fill it in.
/// </summary>
public abstract class SubstitutableGame : IGame
{
    public GameId GameId { get; set; } = HashRows.OwnGameId;

    public abstract StoreIdentifiers StoreIdentifiers { get; }
    public abstract string DisplayName { get; }
    public abstract Optional<NexusModsGameId> NexusModsGameId { get; }
    public abstract IStreamFactory IconImage { get; }
    public abstract IStreamFactory TileImage { get; }
    public abstract ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult);
    public abstract GamePath GetPrimaryFile(GameInstallation installation);
    public abstract ILibraryItemInstaller[] LibraryItemInstallers { get; }
    public abstract IDiagnosticEmitter[] DiagnosticEmitters { get; }
    public abstract ILoadoutSynchronizer Synchronizer { get; }
    public abstract ISortOrderManager SortOrderManager { get; }
}
