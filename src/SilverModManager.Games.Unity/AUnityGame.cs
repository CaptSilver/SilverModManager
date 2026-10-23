using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.Unity.BepInEx;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity;

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
            provider.GetRequiredService<BepInExMissingEmitter>(),
            provider.GetRequiredService<WineDllOverrideEmitter>(),
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
