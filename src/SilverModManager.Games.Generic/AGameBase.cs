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
using SilverModManager.Sdk.Games.Versions;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.Generic;

/// <summary>
/// What a game needs beyond its identity: art from the store ids, a synchronizer, installers, sort orders and the
/// diagnostics every game gets. A game supplies the factories and its locations.
/// </summary>
/// <remarks>
/// Not generic, so a game built from a data record can use it. A hand-written game goes through
/// <see cref="AGame{TSelf}"/>, which forwards the static identity members <see cref="IGameData{TSelf}"/> declares.
/// Everything built from a virtual member is lazy: a derived constructor body has not run when this
/// constructor does, so reading an overridable member here would see unset state.
/// </remarks>
public abstract class AGameBase : IGame
{
    private readonly Lazy<IStreamFactory> _icon;
    private readonly Lazy<IStreamFactory> _tile;
    private readonly Lazy<ILibraryItemInstaller[]> _installers;
    private readonly Lazy<IDiagnosticEmitter[]> _emitters;
    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    private readonly Lazy<ISortOrderManager> _sortOrderManager;

    protected AGameBase(IServiceProvider provider)
    {
        Provider = provider;
        _icon = new Lazy<IStreamFactory>(() => provider.GetRequiredService<IGameArtSource>().Icon(ArtRequest()));
        _tile = new Lazy<IStreamFactory>(() => provider.GetRequiredService<IGameArtSource>().Tile(ArtRequest()));
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

    private GameArtRequest ArtRequest() => new(GameIdValue, DisplayNameValue, StoreIdentifiers.SteamAppIds);

    // Explicit, and behind differently named members, because a hand-written game declares static members named
    // GameId, DisplayName and NexusModsGameId, which would hide public instance ones of the same name.
    GameId IGameData.GameId => GameIdValue;
    string IGameData.DisplayName => DisplayNameValue;
    Optional<Sdk.NexusModsApi.NexusModsGameId> IGameData.NexusModsGameId => NexusModsGameIdValue;

    protected abstract GameId GameIdValue { get; }
    protected abstract string DisplayNameValue { get; }
    protected abstract Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameIdValue { get; }

    // Virtual because a default interface member is only replaced by a public member of a class that lists the
    // interface. This class lists IGame, so a defined game, which lists nothing itself, overrides these instead.
    public virtual bool HasNativeLinuxBuild => false;
    public virtual RelativePath? NativeLinuxPrimaryFile => null;
    public virtual IReadOnlyDictionary<string, string> LocationNames => ImmutableDictionary<string, string>.Empty;
    public virtual IReadOnlyDictionary<string, string> LocationSentenceNames => ImmutableDictionary<string, string>.Empty;

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
        provider.GetRequiredService<OldVcRuntimeEmitter>(),
        provider.GetRequiredService<OpenXrRuntimeEmitter>(),
    ];

    public abstract ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult);

    public virtual GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, PrimaryExe);

    // Declared here because a default interface member can't be overridden by a subclass that doesn't
    // list the interface itself, and a title may need a different version source.
    public virtual Optional<Version> GetLocalVersion(GameInstallation installation)
    {
        try
        {
            return GameExecutableVersion.Read(installation.Locations.ToAbsolutePath(GetPrimaryFile(installation)));
        }
        catch (Exception)
        {
            return Optional<Version>.None;
        }
    }
}
