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
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.RedEngine.ModInstallers;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

[UsedImplicitly]
public class Cyberpunk2077Game : IGame, IGameData<Cyberpunk2077Game>, IWineDllOverrideRequirements, IGameLauncherSkip
{
    public static GameId GameId { get; } = GameId.From("RedEngine.Cyberpunk2077");
    public static string DisplayName => "Cyberpunk 2077";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(3333);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [1091500u],
        GOGProductIds = [2093619782L, 1423049311L],
        EGSCatalogItemId = ["5beededaad9743df90e8f07d92df153f"],
    };

    public IStreamFactory IconImage { get; }
    public IStreamFactory TileImage { get; }

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public Cyberpunk2077Game(IServiceProvider provider)
    {
        var art = provider.GetRequiredService<IGameArtSource>();
        IconImage = art.Icon(StoreIdentifiers.SteamAppIds);
        TileImage = art.Tile(StoreIdentifiers.SteamAppIds);
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new Cyberpunk2077Synchronizer(provider));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = provider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties(
                sortOrderVarieties: [
                    provider.GetRequiredService<RedModSortOrderVariety>(),
                ],
                game: this
            );

            return sortOrderManager;
        });

        DiagnosticEmitters =
        [
            new NoWayToSourceFilesOnDisk(),
            new UndeployableLoadoutDueToMissingGameFiles(provider),
            new PatternBasedDependencyEmitter(PatternDefinitions.Definitions, provider),
            new NoWayToRunRedModEmitter(provider),
            new MissingRedModEmitter(),
            new WinePrefixRequirementsEmitter(this, this),
            provider.GetRequiredService<ProtonPrefixEmitter>(),
        ];

        LibraryItemInstallers =
        [
            FomodXmlInstaller.Create(provider, new GamePath(LocationId.Game, "")),
            new RedModInstaller(provider),
            new SimpleOverlayModInstaller(provider),
            new AppearancePresetInstaller(provider),
            new FolderlessModInstaller(provider),
        ];
    }

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>()
        {
            { LocationId.Game, gameLocatorResult.Path },
            // Skip managing saves for now, to prevent accidental deletion of saves
            // e.g. when removing loadouts, un-managing the game, or uninstalling the app
            // {
            //     LocationId.Saves,
            //     fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine("Saved Games/CD Projekt Red/Cyberpunk 2077")
            // },
            {
                LocationId.AppData,
                fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Combine("CD Projekt Red/Cyberpunk 2077")
            }
        }.ToImmutableDictionary();
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "bin/x64/Cyberpunk2077.exe");

    // https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-users/users-modding-cyberpunk-2077/modding-on-linux
    private static readonly WineDllOverride[] RequiredOverrides =
    [
        new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
        new("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
    ];

    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout) => RequiredOverrides;

    // Steam's %command% is REDprelauncher.exe, which opens its own window unless it is told to start the game.
    public IReadOnlyList<string> LauncherSkipArguments { get; } = ["--launcher-skip"];
}
