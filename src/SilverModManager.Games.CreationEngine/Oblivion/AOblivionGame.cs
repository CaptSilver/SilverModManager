using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.PluginLists;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Oblivion;

/// <summary>
/// What a game running on Oblivion's engine has in common with it. Oblivion orders its plugins by file
/// modification time and reads the active ones from Plugins.txt, which sits in AppData unless Oblivion.ini sets
/// <c>bUseMyGamesDirectory=0</c>, and then in the game folder. The launcher writes plugins.txt and the game reads
/// Plugins.txt; they are one file on the prefix's case-insensitive view. Nehrim is installed over Oblivion and is
/// one of these.
/// </summary>
public abstract class AOblivionGame<TSelf>(IServiceProvider provider) : ACreationEngineGame<TSelf>(provider), IHasIniFiles, ITimestampOrderedGame
    where TSelf : AOblivionGame<TSelf>, IGameData<TSelf>
{
    public override PluginFormat HeaderFormat => PluginFormat.Tes4Oblivion;
    public override GamePath PluginsFile => new(LocationId.AppData, "Plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly TimestampMechanism Mechanism = new(locationFollowsIni: true);

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        PlanPluginTimestamps(loadout, syncTree);

    // libloadorder game_settings.rs hardcoded_plugins: none for Oblivion. Nehrim's primaryPlugins in MO2 only lock
    // its plugins in the UI; listing them here would load Translation.esp, which is not a master, ahead of every
    // master.
    public override IReadOnlyList<string> HardcodedPlugins => [];

    protected override RelativePath PrimaryExe => "Oblivion.exe";

    /// <inheritdoc />
    protected override string[] SaveExtensions => [".ess", ".obse"];

    // The game only reads sLocalSavePath once it looks in My Games at all.
    protected override bool SaveRedirectNeedsMyGamesDirectory => true;

    protected override string[] StopPatterns => ["(^|/)obse(/|$)"];
    protected override string[] EngineFiles =>
    [
        @"obse_loader\.exe",
        @"obse_1_2_416\.dll",
        @"obse_steam_loader\.dll",
        @"obse_editor_1_2\.dll",
    ];
}
