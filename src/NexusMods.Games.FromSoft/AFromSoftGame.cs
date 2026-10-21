using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft;

/// <summary>
/// What every FromSoft game shares. It implements <see cref="IFromSoftGame"/> only: the concrete game lists
/// <see cref="IGameData{TSelf}"/> and owns the three statics, as every game in the app does, and this class reads
/// them through <typeparamref name="TSelf"/>.
/// </summary>
public abstract class AFromSoftGame<TSelf> : IFromSoftGame
    where TSelf : AFromSoftGame<TSelf>, IGameData<TSelf>
{
    protected IServiceProvider Provider { get; }

    public FromSoftGameDefinition Definition { get; }

    GameId IGameData.GameId => TSelf.GameId;
    string IGameData.DisplayName => TSelf.DisplayName;
    Optional<Sdk.NexusModsApi.NexusModsGameId> IGameData.NexusModsGameId => TSelf.NexusModsGameId;

    public StoreIdentifiers StoreIdentifiers { get; }

    public IStreamFactory IconImage { get; }
    public IStreamFactory TileImage { get; }

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public IFromSoftModLoader Loader { get; }

    /// <summary>Empty unless the game has a file of its configuration layer that is written differently per session.</summary>
    public virtual IReadOnlyList<IConfigurationLayerTransform> LayerTransforms => [];

    /// <param name="loader">
    /// Builds the game's mod loader. It is called once the rest of the base is set, because a subclass field
    /// initializer cannot see <paramref name="provider"/> and the installers need the loader before the constructor ends.
    /// </param>
    protected AFromSoftGame(IServiceProvider provider, FromSoftGameDefinition definition, Func<IFromSoftGame, IFromSoftModLoader> loader)
    {
        Provider = provider;
        Definition = definition;

        StoreIdentifiers = new StoreIdentifiers(TSelf.GameId) { SteamAppIds = [definition.Layout.SteamAppId] };

        var art = provider.GetRequiredService<IGameArtSource>();
        IconImage = art.Icon(StoreIdentifiers.SteamAppIds);
        TileImage = art.Tile(StoreIdentifiers.SteamAppIds);

        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => CreateSynchronizer(provider));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = provider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties([], this);
            return sortOrderManager;
        });

        DiagnosticEmitters =
        [
            new NoWayToSourceFilesOnDisk(),
            new UndeployableLoadoutDueToMissingGameFiles(provider),
            provider.GetRequiredService<ProtonPrefixEmitter>(),
            ..provider.GetServices<IFromSoftDiagnosticEmitter>(),
        ];

        Loader = loader(this);
        LibraryItemInstallers = CreateInstallers(provider);
    }

    protected abstract ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider);

    /// <summary>Called last in the constructor, once <see cref="Definition"/>, <see cref="Loader"/> and everything else is set.</summary>
    protected abstract ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider);

    /// <remarks>
    /// <see cref="LocationId.Game"/> is the Steam install folder, not the game's subfolder, because the depot
    /// manifests spell their paths relative to the install folder. The loader's locations are on the host whichever
    /// file system is passed in, which for a Proton game is the game's view of the machine and maps nothing of the host.
    /// Saves stay unmapped: removing a loadout or un-managing the game must never be able to delete them.
    /// </remarks>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var settings = Provider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>();
        var hostFileSystem = Provider.GetRequiredService<IFileSystem>();
        var installationFolder = FromSoftPaths.InstallationFolderName(Definition.Layout, gameLocatorResult);

        return Loader.Locations(settings, hostFileSystem, installationFolder).Add(LocationId.Game, gameLocatorResult.Path);
    }

    /// <remarks>
    /// The anti-cheat launcher when the game has one, because that is what Steam starts: a launch that keeps Steam's
    /// command for an unmodded loadout then finds its own exe in the slot and leaves it there, instead of starting
    /// the game's executable offline.
    /// </remarks>
    public GamePath GetPrimaryFile(GameInstallation installation)
        => new(LocationId.Game, $"{Definition.Layout.GameSubfolder}/{Definition.Layout.Launcher ?? Definition.Layout.GameExecutable}");

    /// <summary>Read from the game's own executable; a launcher carries the anti-cheat's version, not the game's.</summary>
    public Optional<Version> GetLocalVersion(GameInstallation installation)
    {
        try
        {
            var executable = FromSoftPaths.GameFolder(Definition.Layout, installation) / Definition.Layout.GameExecutable;
            return executable.FileInfo.GetFileVersionInfo().ProductVersion;
        }
        catch (Exception)
        {
            return Optional<Version>.None;
        }
    }
}
