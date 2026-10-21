using NexusMods.Games.TestFramework.Helpers;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// Elden Ring as Steam installs it: a <c>Game</c> folder with the executable, the archives and the DLLs, and two
/// artbook folders beside it. The dirty variant adds what an earlier mod manager leaves in <c>Game</c>.
/// </summary>
internal static class EldenRingInstall
{
    public const uint AppId = 1245620;
    public const uint GameDepotId = 1245621;
    public const ulong GameManifestId = 8000000000000000001;

    public static SteamFile[] GameFiles { get; } =
    [
        new("Game/eldenring.exe", "game executable"),
        new("Game/start_protected_game.exe", "anti-cheat launcher"),
        new("Game/regulation.bin", "regulation"),
        new("Game/Data0.bdt", "data archive"),
        new("Game/Data0.bhd", "data archive header"),
        new("Game/sd/sd.bdt", "sound archive"),
        new("Game/movie/intro.bk2", "intro video"),
        new("Game/oo2core_6_win64.dll", "oodle"),
        new("Game/steam_api64.dll", "steam api"),
        new("ArtbookOST/readme.txt", "artbook"),
        new("ERD_ArtbookOST/readme.txt", "artbook"),
    ];

    /// <summary>The files a sync is expected to track: the small ones, with no archive or video.</summary>
    public static string[] IndexedGameFiles { get; } =
    [
        "Game/eldenring.exe", "Game/start_protected_game.exe", "Game/regulation.bin",
        "Game/oo2core_6_win64.dll", "Game/steam_api64.dll",
    ];

    public static SteamInstall Install(SteamLibraryFixture library, bool dirty)
    {
        var install = library.AddGame(AppId, "ELDEN RING", "ELDEN RING", 15000000, new SteamDepot(GameDepotId, GameManifestId, GameFiles));
        if (dirty) AddLeftoversOfAnotherModManager(library, install);
        return install;
    }

    /// <summary>
    /// What an install managed by a launcher script looks like: the anti-cheat launcher swapped for the game's
    /// executable with the original kept beside it, a graphics hook linked in, mod folders and crash dumps.
    /// </summary>
    private static void AddLeftoversOfAnotherModManager(SteamLibraryFixture library, SteamInstall install)
    {
        SteamLibraryFixture.WriteExtra(install, "Game/start_protected_game.exe.erm-backup", "anti-cheat launcher");
        SteamLibraryFixture.WriteExtra(install, "Game/start_protected_game.exe", "game executable");
        SteamLibraryFixture.WriteExtra(install, "Game/mods/AdjustTheFov/config.ini", "[fov]\nvalue=1.2");
        SteamLibraryFixture.WriteExtra(install, "Game/SeamlessCoop/crashdumps/x.dmp", "dump");

        var hook = library.Root / "reshade" / "ReShade64.dll";
        hook.Parent.CreateDirectory();
        File.WriteAllText(hook.ToString(), "hook");
        File.CreateSymbolicLink((install.Folder / "Game/dxgi.dll").ToString(), hook.ToString());
    }
}
