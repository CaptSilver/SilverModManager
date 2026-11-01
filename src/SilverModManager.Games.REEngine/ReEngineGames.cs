using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.REEngine;

/// <summary>Every game that is a <see cref="ReEngineGameDefinition"/> row. Adding a game is adding a row.</summary>
/// <remarks>
/// Steam ids, executables and store names follow each app's Steam launch entry (api.steamcmd.net/v1/info/&lt;appid&gt;);
/// release names are the asset names of REFramework's latest GitHub release (api.github.com/repos/praydog/REFramework/releases/latest);
/// Nexus ids come from the Nexus games list, https://data.nexusmods.com/file/nexus-data/games.json, by domain.
/// </remarks>
public static class ReEngineGames
{
    public static readonly ReEngineGameDefinition DevilMayCry5 = new()
    {
        GameId = GameId.From("ReEngine.DevilMayCry5"),
        DisplayName = "Devil May Cry 5",
        NexusModsGameId = NexusModsGameId.From(2751),
        Executable = "DevilMayCry5.exe",
        SteamAppId = 601150,
        ReFrameworkRelease = "DMC5.zip",
    };

    public static readonly ReEngineGameDefinition ResidentEvil2 = new()
    {
        GameId = GameId.From("ReEngine.ResidentEvil2"),
        DisplayName = "Resident Evil 2 (2019)",
        NexusModsGameId = NexusModsGameId.From(2702),
        Executable = "re2.exe",
        SteamAppId = 883710,
        ReFrameworkRelease = "RE2.zip",
    };

    public static readonly ReEngineGameDefinition ResidentEvil3 = new()
    {
        GameId = GameId.From("ReEngine.ResidentEvil3"),
        DisplayName = "Resident Evil 3 (2020)",
        NexusModsGameId = NexusModsGameId.From(3191),
        Executable = "re3.exe",
        SteamAppId = 952060,
        ReFrameworkRelease = "RE3.zip",
    };

    public static readonly ReEngineGameDefinition MonsterHunterRise = new()
    {
        GameId = GameId.From("ReEngine.MonsterHunterRise"),
        DisplayName = "Monster Hunter Rise",
        NexusModsGameId = NexusModsGameId.From(4095),
        Executable = "MonsterHunterRise.exe",
        SteamAppId = 1446780,
        ReFrameworkRelease = "MHRISE.zip",
    };

    public static readonly ReEngineGameDefinition MonsterHunterWilds = new()
    {
        GameId = GameId.From("ReEngine.MonsterHunterWilds"),
        DisplayName = "Monster Hunter Wilds",
        NexusModsGameId = NexusModsGameId.From(6993),
        Executable = "MonsterHunterWilds.exe",
        SteamAppId = 2246340,
        ReFrameworkRelease = "MHWILDS.zip",
    };

    public static readonly IReadOnlyList<ReEngineGameDefinition> All =
        [DevilMayCry5, ResidentEvil2, ResidentEvil3, MonsterHunterRise, MonsterHunterWilds];
}
