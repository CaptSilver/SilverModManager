using System.Collections.Immutable;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.EldenRing;

[UsedImplicitly]
public class EldenRingGame : IGame, IGameData<EldenRingGame>
{
    public static GameId GameId { get; } = GameId.From("FromSoft.EldenRing");
    public static string DisplayName => "Elden Ring";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(4333);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [EldenRingPaths.SteamAppId],
    };

    public IStreamFactory IconImage { get; }
    public IStreamFactory TileImage { get; }

    private readonly IServiceProvider _provider;
    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public EldenRingGame(IServiceProvider provider)
    {
        _provider = provider;

        var art = provider.GetRequiredService<IGameArtSource>();
        IconImage = art.Icon(StoreIdentifiers.SteamAppIds);
        TileImage = art.Tile(StoreIdentifiers.SteamAppIds);

        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new EldenRingSynchronizer(provider));
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
            ..provider.GetServices<IEldenRingDiagnosticEmitter>(),
        ];

        // No FOMOD installer: Elden Ring FOMODs assume the game folder, which nothing is ever installed into.
        LibraryItemInstallers =
        [
            // First, because the release also holds a lone DLL that the native installer would take for a mod.
            new Me3HostInstaller(provider),
            new SeamlessCoopInstaller(provider),
            new Me3PackageInstaller(provider),
            new Me3NativeInstaller(provider),
        ];
    }

    /// <remarks>
    /// <see cref="LocationId.Game"/> is the Steam install folder, not its <c>Game</c> subfolder, because the depot
    /// manifests spell their paths relative to the install folder. The me3 locations are on the host whichever file
    /// system is passed in, which for a Proton game is the game's view of the machine and maps nothing of the host.
    /// </remarks>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var settings = _provider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>();
        var hostFileSystem = _provider.GetRequiredService<IFileSystem>();
        var installationId = gameLocatorResult.Store == GameStore.Steam ? gameLocatorResult.StoreIdentifier : EldenRingPaths.SteamAppId.ToString();

        // Saves stay unmapped: removing a loadout or un-managing the game must never be able to delete them.
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path },
            { LocationId.Me3Mods, EldenRingPaths.Me3Mods(settings, hostFileSystem, installationId) },
            { LocationId.Me3Host, EldenRingPaths.Me3Host(settings, hostFileSystem, installationId) },
        }.ToImmutableDictionary();
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, $"{EldenRingPaths.GameSubfolder}/eldenring.exe");
}
