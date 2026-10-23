using SilverModManager.Games.TestFramework.Helpers;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Cyberpunk 2077 as Steam installs it: the game's own depot and the Phantom Liberty depot, with an older
/// manifest of the game depot left beside the current one, plus the files a player adds to the folder.
/// </summary>
internal static class CyberpunkSteamInstall
{
    public const uint AppId = 1091500;
    public const uint GameDepotId = 1091501;
    public const uint ExpansionDepotId = 2138330;
    public const ulong GameManifestId = 7000000000000000001;
    public const ulong ExpansionManifestId = 7000000000000000002;
    public const ulong SupersededGameManifestId = 6000000000000000001;

    public static SteamFile[] GameFiles { get; } =
    [
        new("bin/x64/Cyberpunk2077.exe", "game executable"),
        new("bin/x64/REDprelauncher.exe", "prelauncher"),
        new("archive/pc/content/basegame_1_engine.archive", "engine archive"),
        new("archive/pc/content/basegame_2_mainmenu.archive", "menu archive"),
        new("r6/config/inputContexts.xml", "<contexts/>"),
        new("engine/config/platform/pc/user_settings_default.json", "{}"),
    ];

    public static SteamFile[] ExpansionFiles { get; } =
    [
        new("archive/pc/ep1/ep1_1_engine.archive", "expansion archive"),
        new("archive/pc/ep1/ep1_2_audio.archive", "expansion audio"),
    ];

    public static SteamDepot GameDepot { get; } = new(GameDepotId, GameManifestId, GameFiles);

    /// <summary>The expansion's manifest is the one in the library's own depotcache, so both lookups are used.</summary>
    public static SteamDepot ExpansionDepot { get; } = new(ExpansionDepotId, ExpansionManifestId, ExpansionFiles, ManifestCache.Library);

    public static SteamInstall Install(SteamLibraryFixture library, params SteamDepot[] depots)
    {
        var install = library.AddGame(AppId, "Cyberpunk 2077", "Cyberpunk 2077", 14000000, depots.Length == 0 ? [GameDepot, ExpansionDepot] : depots);
        library.AddSupersededManifest(new SteamDepot(GameDepotId, SupersededGameManifestId, [new SteamFile("bin/x64/Cyberpunk2077.exe", "an older executable")]));
        return install;
    }

    public static void AddPlayerFiles(SteamInstall install)
    {
        SteamLibraryFixture.WriteExtra(install, "archive/pc/mod/appearance.archive", "mod archive");
        SteamLibraryFixture.WriteExtra(install, "r6/scripts/tweak.reds", "// mod script");
        SteamLibraryFixture.WriteExtra(install, "bin/x64/plugins/cyber_engine_tweaks.log", "log line");
    }
}
