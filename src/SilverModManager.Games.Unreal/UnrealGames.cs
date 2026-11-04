using DynamicData.Kernel;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.Unreal;

/// <summary>
/// Every game that is an <see cref="UnrealGameDefinition"/> row. Adding a game is adding a row.
/// </summary>
/// <remarks>
/// Steam ids, store names and launch executables follow each app's Steam launch entry (api.steamcmd.net/v1/info/&lt;appid&gt;);
/// project folders follow the Mod Organizer 2 basic_games plugins and Fluorine Manager's copies of them; Nexus ids come
/// from the Nexus games list, https://data.nexusmods.com/file/nexus-data/games.json, by domain.
/// </remarks>
public static class UnrealGames
{
    public static readonly UnrealGameDefinition FinalFantasy7Remake = new()
    {
        GameId = GameId.From("Unreal.FinalFantasy7Remake"),
        DisplayName = "Final Fantasy VII Remake",
        NexusModsGameId = NexusModsGameId.From(4202),
        Executable = "ff7remake.exe",
        ProjectFolder = "End",
        SteamAppIds = [1462040],
    };

    public static readonly UnrealGameDefinition FinalFantasy7Rebirth = new()
    {
        GameId = GameId.From("Unreal.FinalFantasy7Rebirth"),
        DisplayName = "Final Fantasy VII Rebirth",
        NexusModsGameId = NexusModsGameId.From(7237),
        Executable = "ff7rebirth.exe",
        ProjectFolder = "End",
        SteamAppIds = [2909400],
    };

    public static readonly UnrealGameDefinition Stalker2 = new()
    {
        GameId = GameId.From("Unreal.Stalker2"),
        DisplayName = "S.T.A.L.K.E.R. 2: Heart of Chornobyl",
        NexusModsGameId = NexusModsGameId.From(6944),
        Executable = "Stalker2.exe",
        ProjectFolder = "Stalker2",
        SteamAppIds = [1643320],
        GogProductIds = [1529799785],
    };

    public static readonly UnrealGameDefinition Palworld = new()
    {
        GameId = GameId.From("Unreal.Palworld"),
        DisplayName = "Palworld",
        NexusModsGameId = NexusModsGameId.From(6063),
        Executable = "Palworld.exe",
        ProjectFolder = "Pal",
        SteamAppIds = [1623730],
    };

    public static readonly UnrealGameDefinition ReadyOrNot = new()
    {
        GameId = GameId.From("Unreal.ReadyOrNot"),
        DisplayName = "Ready or Not",
        NexusModsGameId = NexusModsGameId.From(4205),
        Executable = "ReadyOrNot.exe",
        ProjectFolder = "ReadyOrNot",
        SteamAppIds = [1144200],
    };

    public static readonly UnrealGameDefinition Dispatch = new()
    {
        GameId = GameId.From("Unreal.Dispatch"),
        DisplayName = "Dispatch",
        NexusModsGameId = NexusModsGameId.From(8325),
        Executable = "Dispatch.exe",
        ProjectFolder = "Dispatch",
        SteamAppIds = [2592160],
    };

    // Steam launches HogwartsLegacy.exe at the game root (default launch entry of app 990080), which starts
    // Phoenix/Binaries/Win64/HogwartsLegacy.exe. The game is mostly modded with UE4SS 2.x, whose Mods folder sits
    // beside that binary. The Epic Games Store sells it too, but no public source for its catalogue item id was
    // found, so no Epic id is recorded.
    public static readonly UnrealGameDefinition HogwartsLegacy = new()
    {
        GameId = GameId.From("Unreal.HogwartsLegacy"),
        DisplayName = "Hogwarts Legacy",
        NexusModsGameId = NexusModsGameId.From(5113),
        Executable = "HogwartsLegacy.exe",
        ProjectFolder = "Phoenix",
        SteamAppIds = [990080],
        Ue4ssLayout = Ue4ssLayout.Ue4ss2,
    };

    public static readonly IReadOnlyList<UnrealGameDefinition> All =
        [FinalFantasy7Remake, FinalFantasy7Rebirth, Stalker2, Palworld, ReadyOrNot, Dispatch, HogwartsLegacy];
}
