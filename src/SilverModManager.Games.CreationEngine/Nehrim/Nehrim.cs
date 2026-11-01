using SilverModManager.Abstractions.Games;
using DynamicData.Kernel;
using SilverModManager.Games.CreationEngine.Oblivion;
using NexusMods.Paths;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Nehrim;

/// <summary>
/// Nehrim: At Fate's Edge, a total conversion that is installed into an Oblivion folder and starts Oblivion.exe.
/// The folder holds <c>Data/Nehrim.esm</c> only once Nehrim is installed, which is what tells it from plain
/// Oblivion on the shared Steam app id. The standalone Steam release (app 1014940) is not covered: its launch
/// entry is NehrimInstallSwitch.exe, and its folder layout is unconfirmed.
/// </summary>
public class Nehrim(IServiceProvider provider) : AOblivionGame<Nehrim>(provider), IGameData<Nehrim>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.Nehrim");
    public static string DisplayName => "Nehrim: At Fate's Edge";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(3312);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/SulfurNitride/Fluorine-Manager libs/game_bethesda/src/games/nehrim/gamenehrim.cpp
        // (steamAPPId is Oblivion's)
        SteamAppIds = [22330u],
    };

    // gamenehrim.cpp primaryPlugins: the master file every Nehrim install has and Oblivion does not.
    public override IReadOnlyList<RelativePath> IdentifyingFiles => ["Data/Nehrim.esm"];

    // No Nehrim masterlist is bundled (github.com/loot has no nehrim repository), so Oblivion's applies.
    public override LootGame LootGame(GameInstallation installation) => Bethesda.Loot.Masterlists.LootGame.Oblivion;

    // gamenehrim.cpp primaryPlugins
    public override IReadOnlyList<string> HardcodedPlugins => ["Nehrim.esm", "Translation.esp"];

    // gamenehrim.cpp: plugins.txt is copied from localAppFolder() + "/Oblivion" (localAppName), while My Games
    // follows gameName(), "Nehrim".
    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Oblivion";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Nehrim";

    protected override string[] GameAliases => ["Nehrim", "Oblivion", "TES4"];
}
