using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Games.Unity.BepInEx;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Unity;

/// <summary>
/// What every Unity game shares. It implements <see cref="IUnityGame"/> only: the concrete game lists
/// <see cref="IGameData{TSelf}"/> and owns the three statics, as every game in the app does, and this class reads
/// them through <typeparamref name="TSelf"/>.
/// </summary>
public abstract class AUnityGame<TSelf> : IUnityGame
    where TSelf : AUnityGame<TSelf>, IGameData<TSelf>
{
    protected IServiceProvider Provider { get; }

    public UnityGameDefinition Definition { get; }

    public BepInExLoader Loader { get; }

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

    public string ThunderstoreCommunity => Definition.ThunderstoreCommunity;
    public string ThunderstoreLoaderPackage => Definition.ThunderstoreLoaderPackage;
    public IReadOnlyList<string> ThunderstoreLoaderAliases => Definition.ThunderstoreLoaderAliases;

    protected AUnityGame(IServiceProvider provider, UnityGameDefinition definition)
    {
        Provider = provider;
        Definition = definition;

        StoreIdentifiers = new StoreIdentifiers(TSelf.GameId) { SteamAppIds = [definition.SteamAppId] };

        var art = provider.GetRequiredService<IGameArtSource>();
        IconImage = art.Icon(StoreIdentifiers.SteamAppIds);
        TileImage = art.Tile(StoreIdentifiers.SteamAppIds);

        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new UnityGameSynchronizer(provider));
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
        ];

        // The installers hold the loader, so it has to exist first.
        Loader = new BepInExLoader(definition);
        LibraryItemInstallers = Loader.Installers(provider);
    }

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, gameLocatorResult.Path } }.ToImmutableDictionary();

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, Definition.Executable);

    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout) => Loader.RequiredWineDllOverrides(loadout);
}
