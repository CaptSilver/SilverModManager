using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.CreationEngine.Creations;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Fallout4;

/// <summary>
/// What a game running on Fallout 4's install has in common with it: the executable, the plugin list, the base
/// masters, F4SE and the My Games and AppData folders. Fallout 4 London is installed over Fallout 4 and is one
/// of these, told apart from it by <see cref="ABethesdaGame{TSelf}.IdentifyingFiles"/>.
/// </summary>
public abstract class AFallout4Game<TSelf>(IServiceProvider provider) : ACreationEngineGame<TSelf>(provider), IHasIniFiles, IGameUpdateSensitivity
    where TSelf : AFallout4Game<TSelf>, IGameData<TSelf>
{
    public TimeSpan? DateWindow => TimeSpan.FromDays(30);
    public string WhatBreaks => "F4SE and mods with a DLL in Data/F4SE/Plugins are built for one game version, so they may need a new release after an update. ESP/ESM plugins, archives and textures are unaffected.";
    public NamedLink? ExtenderPage => new Uri("https://f4se.silverlock.org/").WithName("F4SE");

    protected override RelativePath PrimaryExe => "Fallout4.exe";

    /// <inheritdoc />
    protected override string[] SaveExtensions => [".fos", ".f4se"];
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Fallout4;
    public override GamePath PluginsFile => Fallout4KnownPaths.PluginsFile;
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new AsteriskMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => [
        "Fallout4.esm",
        "DLCRobot.esm",
        "DLCworkshop01.esm",
        "DLCCoast.esm",
        "DLCworkshop02.esm",
        "DLCworkshop03.esm",
        "DLCNukaWorld.esm",
        "DLCUltraHighResolution.esm",
    ];
    public override bool SupportsLightPlugins => true;
    public override bool MasterByExtension => true;
    public override GamePath? CreationClubListFile => new(LocationId.Game, "Fallout4.ccc");
    public override IReadOnlyList<GamePath> TestFileIniFiles => [new(LocationId.Preferences, "Fallout4.ini"), new(LocationId.Preferences, "Fallout4Custom.ini")];

    // Not checked against an installed Fallout 4: the catalog and manifest locations are taken from Skyrim
    // Special Edition, which shares the format.
    private readonly CreationsCatalogRecognizer _gameInstalledContent = new CreationsCatalogRecognizer(
        new GamePath(LocationId.AppData, "ContentCatalog.txt"),
        new GamePath(LocationId.Game, "Creations"),
        new GamePath(LocationId.Game, "Data"),
        "Fallout4Creation");

    protected override IGameInstalledContentRecognizer? GameInstalledContent => _gameInstalledContent;

    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Fallout4";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Fallout4";

    protected override string[] GameAliases => ["Fallout 4", "Fallout4", "FO4", "F4"];
    protected override string[] StopPatterns => ["(^|/)f4se(/|$)"];
    protected override string[] EngineFiles =>
    [
        // F4SE
        @"f4se_loader\.exe",
        @"f4se_.*\.dll",
        // Plugin Preloader (new)
        @"winhttp\.dll",
        @"xSE\ PluginPreloader\.xml",
        // Plugin Preloader (old)
        @"IpHlpAPI\.dll",
    ];

    protected override GamePath? NativePluginFolder => DataPath / "F4SE/Plugins";
}
