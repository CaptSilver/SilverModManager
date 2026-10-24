using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Games.Larian.BaldursGate3.Emitters;
using SilverModManager.Games.Larian.BaldursGate3.Installers;
using SilverModManager.Games.Larian.BaldursGate3.ModList;
using SilverModManager.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.BaldursGate3;

public class BaldursGate3 : IGame, IGameData<BaldursGate3>, IWineDllOverrideRequirements
{
    public static GameId GameId { get; } = GameId.From("Larian.BaldursGate3");
    public static string DisplayName => "Baldur's Gate 3";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(3474);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [1086940u],
        GOGProductIds = [1456460669L],
    };

    public IStreamFactory IconImage { get; }
    public IStreamFactory TileImage { get; }

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public BaldursGate3(IServiceProvider provider)
    {
        var art = provider.GetRequiredService<IGameArtSource>();
        IconImage = art.Icon(StoreIdentifiers.SteamAppIds);
        TileImage = art.Tile(StoreIdentifiers.SteamAppIds);
        // One instance, so the synchronizer and the diagnostic read each pak from disk once between them.
        var modules = new Bg3PakModules(provider);
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new BaldursGate3Synchronizer(provider, this, modules));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            var sortOrderManager = provider.GetRequiredService<SortOrderManager>();
            sortOrderManager.RegisterSortOrderVarieties([new Bg3PakSortOrderVariety(provider, this)], this);
            return sortOrderManager;
        });

        DiagnosticEmitters =
        [
            provider.GetRequiredService<DependencyDiagnosticEmitter>(),
            provider.GetRequiredService<ProtonPrefixEmitter>(),
            new DuplicateModuleEmitter(this, modules),
            new Bg3ModListReplacedEmitter(this),
            new Bg3ModListResetEmitter(this),
        ];

        LibraryItemInstallers =
        [
            new BG3SEInstaller(provider),
            new GenericPatternMatchInstaller(provider)
            {
                InstallFolderTargets =
                [
                    // Pak mods
                    // Examples:
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/366?tab=description">ImpUI (ImprovedUI) Patch7Ready</see>
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/11373?tab=description">NPC Visual Overhaul (WIP) - NPC VO</see>
                    new InstallFolderTarget
                    {
                        DestinationGamePath = new GamePath(Bg3Constants.ModsLocationId, ""),
                        KnownValidFileExtensions = [Bg3Constants.PakFileExtension],
                        FileExtensionsToDiscard =
                        [
                            KnownExtensions.Txt, KnownExtensions.Md, KnownExtensions.Pdf, KnownExtensions.Png,
                            KnownExtensions.Json, new Extension(".lnk"),
                        ],
                    },

                    // bin and NativeMods mods
                    // Examples:
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/944">Native Mod Loader</see>
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/668?tab=files">Achievement Enabler</see>
                    new InstallFolderTarget
                    {
                        DestinationGamePath = new GamePath(LocationId.Game, "bin"),
                        KnownSourceFolderNames = ["bin"],
                        Names = ["NativeMods"],
                    },

                    // loose files Data mods
                    // Examples:
                    // - <see href="https://www.nexusmods.com/baldursgate3/mods/555?tab=description">Fast XP</see>
                    new InstallFolderTarget
                    {
                        DestinationGamePath = new GamePath(LocationId.Game, "Data"),
                        KnownSourceFolderNames = ["Data"],
                        Names = ["Generated", "Public"],
                    },
                ],
            },
        ];
    }

    // The official Linux build runs on the host, so a Steam install that also has a Proton prefix from earlier
    // runs is still the native game when bin/bg3 is installed.
    public bool HasNativeLinuxBuild => true;

    public RelativePath? NativeLinuxPrimaryFile => NativeExecutable;

    private static readonly RelativePath NativeExecutable = RelativePath.FromUnsanitizedInput("bin/bg3");

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        // The Windows build keeps its data in the prefix's AppData\Local, which the Wine file system maps for us.
        // On the host file system the same known folder is XDG_DATA_HOME, where the native build keeps it.
        var larian = fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Combine("Larian Studios/Baldur's Gate 3");

        return new Dictionary<LocationId, AbsolutePath>()
        {
            { LocationId.Game, gameLocatorResult.Path },
            { Bg3Constants.ModsLocationId, larian.Combine("Mods") },
            { Bg3Constants.PlayerProfilesLocationId, larian.Combine("PlayerProfiles/Public") },
            { Bg3Constants.ScriptExtenderConfigLocationId, larian.Combine("ScriptExtender") },
        }.ToImmutableDictionary();
    }

    internal static bool IsNativeBuild(GameLocatorResult result) => result.TargetOS.IsLinux && result.WinePrefixPath is null;

    public GamePath GetPrimaryFile(GameInstallation installation)
    {
        if (IsNativeBuild(installation.LocatorResult)) return new GamePath(LocationId.Game, NativeExecutable);

        // Use launcher to allow choosing between DirectX11 and Vulkan on GOG, Steam already always starts the launcher
        return new GamePath(LocationId.Game, "Launcher/LariLauncher.exe");
    }

    // BG3SE ships bin/DWrite.dll, which replaces a system DLL that Wine would otherwise load itself.
    // It is Wine that needs the override, so it applies to every store.
    public IReadOnlyList<WineDllOverride> RequiredWineDllOverrides(Loadout.ReadOnly loadout)
    {
        // The native build has no Wine to configure; the script extender can't load there at all.
        if (IsNativeBuild(loadout.InstallationInstance.LocatorResult)) return [];
        return ScriptExtenderFile.Find(loadout).HasValue ? ScriptExtenderOverrides : [];
    }

    private static readonly WineDllOverride[] ScriptExtenderOverrides =
    [
        new("DWrite", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
    ];
}
