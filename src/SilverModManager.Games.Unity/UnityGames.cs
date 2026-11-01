using DynamicData.Kernel;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Unity;

/// <summary>Every game that is a <see cref="UnityGameDefinition"/> row. Adding a game is adding a row.</summary>
public static class UnityGames
{
    public static readonly UnityGameDefinition Repo = new()
    {
        GameId = GameId.From("Unity.Repo"),
        DisplayName = "R.E.P.O.",
        NexusModsGameId = NexusModsGameId.From(7398),
        Executable = "REPO.exe",
        DataFolder = "REPO_Data",
        SteamAppId = 3241660,
        ThunderstoreCommunity = "repo",
        ThunderstoreLoaderPackage = "BepInEx-BepInExPack",
        ThunderstoreLoaderAliases = ["bbepis-BepInExPack", "xiaoxiao921-BepInExPack"],
        // Patches come in bursts a few days apart and then stop for months, so a shorter window fits.
        DateWindow = TimeSpan.FromDays(14),
    };

    public static readonly UnityGameDefinition Peak = new()
    {
        GameId = GameId.From("Unity.Peak"),
        DisplayName = "PEAK",
        NexusModsGameId = NexusModsGameId.From(7867),
        Executable = "PEAK.exe",
        DataFolder = "PEAK_Data",
        SteamAppId = 3527290,
        ThunderstoreCommunity = "peak",
        ThunderstoreLoaderPackage = "BepInEx-BepInExPack_PEAK",
        ThunderstoreLoaderAliases = ["BepInEx-BepInExPack", "bbepis-BepInExPack", "xiaoxiao921-BepInExPack"],
    };

    // The rows below follow the Steam launch entry of each app (api.steamcmd.net/v1/info/<appid>) and MO2's
    // basic_games plugin for the executable, games.json for the Nexus id, and the community's package list
    // (thunderstore.io/c/<community>/api/v1/package/) for the loader pack and the retired packs mods still name.
    // The data folder is the executable's name with _Data, as the Unity player lays it out.

    public static readonly UnityGameDefinition Valheim = new()
    {
        GameId = GameId.From("Unity.Valheim"),
        DisplayName = "Valheim",
        NexusModsGameId = NexusModsGameId.From(3667),
        Executable = "valheim.exe",
        DataFolder = "valheim_Data",
        SteamAppId = 892970,
        ThunderstoreCommunity = "valheim",
        ThunderstoreLoaderPackage = "denikson-BepInExPack_Valheim",
        ThunderstoreLoaderAliases = ["BepInEx-BepInExPack_Valheim"],
    };

    public static readonly UnityGameDefinition Subnautica = new()
    {
        GameId = GameId.From("Unity.Subnautica"),
        DisplayName = "Subnautica",
        NexusModsGameId = NexusModsGameId.From(1155),
        Executable = "Subnautica.exe",
        DataFolder = "Subnautica_Data",
        SteamAppId = 264710,
        ThunderstoreCommunity = "subnautica",
        ThunderstoreLoaderPackage = "Subnautica_Modding-BepInExPack",
        ThunderstoreLoaderAliases = ["Subnautica_Modding-BepInExPack_Subnautica", "Subnautica_Modding-BepInExPack_Subnautica_Experimental"],
    };

    public static readonly UnityGameDefinition SubnauticaBelowZero = new()
    {
        GameId = GameId.From("Unity.SubnauticaBelowZero"),
        DisplayName = "Subnautica: Below Zero",
        NexusModsGameId = NexusModsGameId.From(2706),
        Executable = "SubnauticaZero.exe",
        DataFolder = "SubnauticaZero_Data",
        SteamAppId = 848450,
        ThunderstoreCommunity = "subnautica-below-zero",
        ThunderstoreLoaderPackage = "Subnautica_Modding-BepInExPack",
        ThunderstoreLoaderAliases = ["Subnautica_Modding-BepInExPack_BelowZero", "BepInEx-BepInExPack"],
    };

    public static readonly UnityGameDefinition HollowKnightSilksong = new()
    {
        GameId = GameId.From("Unity.HollowKnightSilksong"),
        DisplayName = "Hollow Knight: Silksong",
        NexusModsGameId = NexusModsGameId.From(8136),
        Executable = "Hollow Knight Silksong.exe",
        DataFolder = "Hollow Knight Silksong_Data",
        SteamAppId = 1030300,
        ThunderstoreCommunity = "hollow-knight-silksong",
        ThunderstoreLoaderPackage = "silksong_modding-BepInExPack_Silksong",
        // Deprecated in favour of the package above, and still the one most Silksong mods list as a dependency.
        ThunderstoreLoaderAliases = ["BepInEx-BepInExPack_Silksong"],
    };

    public static readonly IReadOnlyList<UnityGameDefinition> All = [Repo, Peak, Valheim, Subnautica, SubnauticaBelowZero, HollowKnightSilksong];
}
