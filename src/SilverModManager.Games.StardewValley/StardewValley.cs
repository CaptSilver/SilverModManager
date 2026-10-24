using System.Collections.Immutable;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.StardewValley.Emitters;
using SilverModManager.Games.StardewValley.Installers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Games.Versions;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.StardewValley;

[UsedImplicitly]
public class StardewValley : IGame, IGameData<StardewValley>
{
    public static GameId GameId { get; } = GameId.From("StardewValley");
    public static string DisplayName => "Stardew Valley";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1303);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [413150u],
        GOGProductIds = [1453375253L],
    };

    public IStreamFactory IconImage { get; }
    public IStreamFactory TileImage { get; }

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public StardewValley(IServiceProvider serviceProvider)
    {
        var art = serviceProvider.GetRequiredService<IGameArtSource>();
        IconImage = art.Icon(StoreIdentifiers.SteamAppIds);
        TileImage = art.Tile(StoreIdentifiers.SteamAppIds);
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new StardewValleyLoadoutSynchronizer(serviceProvider));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = serviceProvider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties([], this);

            return sortOrderManager;
        });

        LibraryItemInstallers =
        [
            FomodXmlInstaller.Create(serviceProvider, new GamePath(LocationId.Game, Constants.ModsFolder)),
            serviceProvider.GetRequiredService<SMAPIInstaller>(),
            serviceProvider.GetRequiredService<GenericInstaller>(),
        ];

        DiagnosticEmitters =
        [
            new NoWayToSourceFilesOnDisk(),
            new UndeployableLoadoutDueToMissingGameFiles(serviceProvider),
            serviceProvider.GetRequiredService<SMAPIGameVersionDiagnosticEmitter>(),
            serviceProvider.GetRequiredService<DependencyDiagnosticEmitter>(),
            serviceProvider.GetRequiredService<MissingSMAPIEmitter>(),
            serviceProvider.GetRequiredService<SMAPIModDatabaseCompatibilityDiagnosticEmitter>(),
            serviceProvider.GetRequiredService<VersionDiagnosticEmitter>(),
            serviceProvider.GetRequiredService<ModOverwritesGameFilesEmitter>(),
        ];
    }

    // Stardew ships a native Linux build, so a Steam install with no Proton prefix is that build.
    public bool HasNativeLinuxBuild => true;

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path },
        }.ToImmutableDictionary();
    }

    public GamePath GetPrimaryFile(GameInstallation installation)
    {
        // NOTE(erri120): Our SMAPI installer overrides all of these files.
        return installation.LocatorResult.TargetOS.IsWindows
            ? new GamePath(LocationId.Game, "Stardew Valley.exe")
            : new GamePath(LocationId.Game, "StardewValley");
    }

    public Optional<GamePath> GetFallbackCollectionInstallDirectory(GameInstallation installation)
    {
        // NOTE(erri120): see https://github.com/Nexus-Mods/NexusMods.App/issues/2553
        return Optional<GamePath>.Create(new GamePath(LocationId.Game, Constants.ModsFolder));
    }

    public Optional<Version> GetLocalVersion(GameInstallation installation) => ReadLauncherVersion(installation).Version;

    /// <summary>
    /// The launcher's version and where it came from. <c>Stardew Valley.dll</c> can lack a usable version resource,
    /// in which case its assembly metadata and finally <c>Stardew Valley.deps.json</c> are consulted.
    /// </summary>
    public LauncherVersion ReadLauncherVersion(GameInstallation installation)
    {
        try
        {
            var launcher = installation.Locations[LocationId.Game].Path.Combine("Stardew Valley.dll");
            return new LauncherVersionReader().Read(launcher);
        }
        catch (Exception)
        {
            return LauncherVersion.Unknown;
        }
    }
}
