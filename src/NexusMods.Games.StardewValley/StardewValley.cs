using System.Collections.Immutable;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.Games.FOMOD;
using NexusMods.Games.StardewValley.Emitters;
using NexusMods.Games.StardewValley.Installers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.StardewValley;

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

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<StardewValley>("NexusMods.Games.StardewValley.Resources.thumbnail.webp");
    public IStreamFactory TileImage { get; } = new EmbeddedResourceStreamFactory<StardewValley>("NexusMods.Games.StardewValley.Resources.tile.webp");

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public StardewValley(IServiceProvider serviceProvider)
    {
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

    public Optional<Version> GetLocalVersion(GameInstallation installation)
    {
        try
        {
            var fileInfo = installation.Locations[LocationId.Game].Path.Combine("Stardew Valley.dll").FileInfo;
            return fileInfo.GetFileVersionInfo().FileVersion;
        }
        catch (Exception)
        {
            return Optional<Version>.None;
        }
    }


}
