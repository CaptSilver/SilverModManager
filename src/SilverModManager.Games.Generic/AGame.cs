using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.Generic;

/// <summary>
/// What a game needs beyond its identity: art from the store ids, a synchronizer, installers, sort orders and the
/// diagnostics every game gets. A game supplies the factories and its locations.
/// </summary>
/// <remarks>
/// Generic because <see cref="IGameData{TSelf}"/> declares the game id, display name and Nexus id as static
/// abstracts; the base forwards its explicit instance members to <typeparamref name="TSelf"/>.
/// Everything built from a virtual member is lazy: a derived constructor body has not run when this
/// constructor does, so reading an overridable member here would see unset state.
/// </remarks>
public abstract class AGame<TSelf> : IGame
    where TSelf : AGame<TSelf>, IGameData<TSelf>
{
    private readonly Lazy<IStreamFactory> _icon;
    private readonly Lazy<IStreamFactory> _tile;
    private readonly Lazy<ILibraryItemInstaller[]> _installers;
    private readonly Lazy<IDiagnosticEmitter[]> _emitters;
    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    private readonly Lazy<ISortOrderManager> _sortOrderManager;

    protected AGame(IServiceProvider provider)
    {
        Provider = provider;
        _icon = new Lazy<IStreamFactory>(() => provider.GetRequiredService<IGameArtSource>().Icon(StoreIdentifiers.SteamAppIds));
        _tile = new Lazy<IStreamFactory>(() => provider.GetRequiredService<IGameArtSource>().Tile(StoreIdentifiers.SteamAppIds));
        _installers = new Lazy<ILibraryItemInstaller[]>(() => CreateInstallers(provider));
        _emitters = new Lazy<IDiagnosticEmitter[]>(() => CreateEmitters(provider).ToArray());
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => CreateSynchronizer(provider));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = provider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties(CreateSortOrderVarieties(provider), this);
            return sortOrderManager;
        });
    }

    protected IServiceProvider Provider { get; }

    GameId IGameData.GameId => TSelf.GameId;
    string IGameData.DisplayName => TSelf.DisplayName;
    Optional<Sdk.NexusModsApi.NexusModsGameId> IGameData.NexusModsGameId => TSelf.NexusModsGameId;

    public abstract StoreIdentifiers StoreIdentifiers { get; }
    public IStreamFactory IconImage => _icon.Value;
    public IStreamFactory TileImage => _tile.Value;

    public ILibraryItemInstaller[] LibraryItemInstallers => _installers.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters => _emitters.Value;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;

    /// <summary>The executable, relative to the game folder.</summary>
    protected abstract RelativePath PrimaryExe { get; }

    protected abstract ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider);

    protected abstract ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider);

    /// <summary>The load orders the Load Order page offers for this game. Most games have none.</summary>
    protected virtual ISortOrderVariety[] CreateSortOrderVarieties(IServiceProvider provider) => [];

    /// <summary>
    /// The diagnostics every game gets. A game that adds its own appends to this rather than replacing it.
    /// </summary>
    protected virtual IEnumerable<IDiagnosticEmitter> CreateEmitters(IServiceProvider provider) =>
    [
        new NoWayToSourceFilesOnDisk(),
        new UndeployableLoadoutDueToMissingGameFiles(provider),
        provider.GetRequiredService<ProtonPrefixEmitter>(),
    ];

    public abstract ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult);

    public virtual GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, PrimaryExe);

    // Declared here because a default interface member can't be overridden by a subclass that doesn't
    // list the interface itself, and a title may need a different version source.
    public virtual Optional<Version> GetLocalVersion(GameInstallation installation)
    {
        try
        {
            var fvi = installation.Locations.ToAbsolutePath(GetPrimaryFile(installation)).FileInfo.GetFileVersionInfo();
            return fvi.ProductVersion;
        }
        catch (Exception)
        {
            return Optional<Version>.None;
        }
    }
}
